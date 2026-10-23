"""Complete, uploads-only discovery. Media downloads use yt-dlp's PornHubIE.

Loaded explicitly by Video Downloader's subscription scanner, never installed
into yt-dlp's automatic plugin directory. Requires no external Python runtime.
"""

import hashlib
import re
from html.parser import HTMLParser
from urllib.parse import parse_qs, urljoin, urlsplit

from yt_dlp.extractor.pornhub import PornHubBaseIE
from yt_dlp.utils import ExtractorError

__all__ = ['VideoDownloaderPornHubChannelIE']

_HOST = r'(?:[a-zA-Z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)'
_PATH = r'/(?P<kind>channels|model|pornstar|users)/(?P<slug>[A-Za-z0-9_-]+)(?:/videos(?:/upload)?)?/?'
_VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'}
_LISTS = {'videoSearchResult', 'mostRecentVideosSection', 'pornstarsVideoSection', 'uploadedVideos'}
_EXCLUDED = re.compile(r'watch.?history|recently.?viewed|recommended|suggested|related.?videos|favorites|favourites', re.I)


def _same_source(origin, target):
    """Permit locale/www redirects within the same site, never another tab or author."""
    left, right = urlsplit(origin), urlsplit(target)
    site = re.search(r'pornhub(?:premium)?\.(?:com|net|org)$', left.hostname or '', re.I)
    return (site is not None and right.scheme == 'https' and not right.username
            and right.port in (None, 443)
            and re.fullmatch(r'(?:[a-z0-9-]+\.)?' + re.escape(site.group()), right.hostname or '', re.I) is not None
            and left.path.rstrip('/') == right.path.rstrip('/'))


class _Page(HTMLParser):
    """Only read video cards inside a known publication list, never the page footer."""

    def __init__(self, html, listing_url, page):
        super().__init__(convert_charrefs=True)
        self.listing_url, self.page = listing_url, page
        self.stack, self.cards, self.next_links, self.page_numbers = [], [], [], set()
        self.list_count, self.total, self.empty, self.more_button = 0, None, False, False
        self.card, self.title_parts = None, []
        self.feed(html)
        self.close()
        if self.stack or self.card is not None:
            raise ExtractorError('Incomplete publication HTML (unclosed elements)', expected=True)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        classes = set((attrs.get('class') or '').split())
        parent = self.stack[-1] if self.stack else {}
        publication = attrs.get('id') in _LISTS
        scoped = publication or parent.get('scoped', False)
        excluded = parent.get('excluded', False) or bool(_EXCLUDED.search((attrs.get('id') or '') + ' ' + ' '.join(classes)))
        pagination = parent.get('pagination', False) or bool(classes & {'pagination', 'page_next', 'page_current', 'page_last'})
        disabled = parent.get('disabled', False) or 'disabled' in classes
        node = dict(tag=tag, scoped=scoped, excluded=excluded, pagination=pagination, disabled=disabled,
                    next=parent.get('next', False) or 'page_next' in classes, title=tag == 'h1' or parent.get('title', False))
        if publication and not excluded:
            if not parent.get('scoped', False):
                self.list_count += 1
            for key in ('data-total-videos', 'data-total-count', 'data-videos-count'):
                if key in attrs:
                    value = attrs[key]
                    if not value or not re.fullmatch(r'\d+', value):
                        raise ExtractorError('Invalid publication count', expected=True)
                    count = int(value)
                    if self.total is not None and self.total != count:
                        raise ExtractorError('Publication counts disagree', expected=True)
                    self.total = count
        if scoped and not excluded:
            if classes & {'noVideos', 'noVideosAvailable', 'noVideosMsg', 'noResults'}:
                self.empty = True
            if classes & {'pcVideoListItem', 'videoBox'} and self.card is None:
                self.card = dict(depth=len(self.stack), ids=set(), title='')
            if self.card is not None:
                key = attrs.get('data-video-vkey') or attrs.get('data-viewkey')
                if key:
                    self._add_key(key)
                if tag == 'a' and attrs.get('href'):
                    target = urlsplit(urljoin(self.listing_url, attrs['href']))
                    values = parse_qs(target.query).get('viewkey', [])
                    if target.path == '/view_video.php' and values:
                        site = re.search(r'pornhub(?:premium)?\.(?:com|net|org)$', urlsplit(self.listing_url).hostname, re.I).group().lower()
                        if not re.fullmatch(r'(?:[a-z0-9-]+\.)?' + re.escape(site), target.hostname or '', re.I) or len(values) != 1:
                            raise ExtractorError('Publication card points outside its source', expected=True)
                        self._add_key(values[0])
                        self.card['title'] = self.card['title'] or (attrs.get('title') or '').strip()
        if attrs.get('id') == 'moreDataBtn' and not disabled:
            self.more_button = True
        relation = (attrs.get('rel') or '').split()
        if tag in ('a', 'link') and attrs.get('href'):
            href = attrs['href']
            is_next = 'next' in relation or node['next']
            if (pagination or is_next) and not disabled:
                target = urlsplit(urljoin(self.listing_url, href))
                if not _same_source(self.listing_url, target.geturl()):
                    if is_next:
                        raise ExtractorError('Next page changes the publication source', expected=True)
                else:
                    values = parse_qs(target.query).get('page', ['1'])
                    if len(values) != 1 or not re.fullmatch(r'[1-9]\d*', values[0]):
                        raise ExtractorError('Invalid pagination index', expected=True)
                    number = int(values[0])
                    self.page_numbers.add(number)
                    if is_next:
                        self.next_links.append(number)
        if tag not in _VOID:
            self.stack.append(node)

    def handle_startendtag(self, tag, attributes):
        self.handle_starttag(tag, attributes)
        if tag not in _VOID:
            self.handle_endtag(tag)

    def _add_key(self, key):
        if not re.fullmatch(r'[a-z0-9]+', key):
            raise ExtractorError('Invalid publication video key', expected=True)
        self.card['ids'].add(key)

    def handle_endtag(self, tag):
        index = next((i for i in range(len(self.stack) - 1, -1, -1) if self.stack[i]['tag'] == tag), None)
        if index is None:
            return
        del self.stack[index:]
        if self.card is not None and len(self.stack) <= self.card['depth']:
            if len(self.card['ids']) != 1:
                raise ExtractorError('A publication card has no unique video URL', expected=True)
            self.cards.append((next(iter(self.card['ids'])), self.card['title']))
            self.card = None

    def handle_data(self, data):
        if self.stack and self.stack[-1].get('title'):
            self.title_parts.append(data)

    def result(self):
        if not self.list_count:
            raise ExtractorError('Publication list not found; login, restriction or page layout changed', expected=True)
        if self.list_count != 1:
            raise ExtractorError('Ambiguous publication lists on the page', expected=True)
        unique = dict(self.cards)
        if not unique and not (self.empty or self.total == 0):
            raise ExtractorError('Empty publication page has no confirmed empty-state marker', expected=True)
        if len(set(self.next_links)) > 1 or any(number != self.page + 1 for number in self.next_links):
            raise ExtractorError('Pagination skips or repeats a page', expected=True)
        has_next = bool(self.next_links)
        if not has_next and (self.more_button or any(number > self.page for number in self.page_numbers)):
            raise ExtractorError('Pagination ended before its last advertised page', expected=True)
        if has_next and not unique:
            raise ExtractorError('Empty page advertises more publications', expected=True)
        return list(unique.items()), has_next, self.total, ' '.join(''.join(self.title_parts).split())


class VideoDownloaderPornHubChannelIE(PornHubBaseIE):
    IE_NAME = 'videodownloader:pornhub:channel'
    IE_DESC = 'Video Downloader: complete Pornhub publication lists'
    _VALID_URL = rf'(?i:https?://(?P<domain>{_HOST})(?::(?:80|443))?){_PATH}(?:[?#].*)?$'

    def _limit(self, name, maximum):
        raw = self._configuration_arg(name, [str(maximum)])[0]
        if not re.fullmatch(r'[1-9]\d*', raw) or not 1 <= int(raw) <= maximum:
            raise ExtractorError(f'Invalid {name} limit', expected=True)
        return int(raw)

    def _page(self, base, number, channel_id):
        for attempt in range(3):
            try:
                html, response = self._download_webpage_handle(
                    base, channel_id, f'Publication page {number}', query={'page': number}, impersonate=True)
                if not _same_source(base, response.url):
                    raise ExtractorError('Publication page redirected to a different source or login', expected=True)
                if len(html.encode('utf-8')) > 8 * 1024 * 1024:
                    raise ExtractorError('Publication HTML exceeds 8 MiB', expected=True)
                return _Page(html, base, number).result()
            except ExtractorError as error:
                if getattr(error.cause, 'status', None) not in (429, 500, 502, 503, 504) or attempt == 2:
                    raise
                self._sleep(2 ** attempt, channel_id)

    def _real_extract(self, url):
        match = self._match_valid_url(url)
        domain, kind, slug = match.group('domain', 'kind', 'slug')
        base_host = re.search(r'pornhub(?:premium)?\.(?:com|net|org)$', domain, re.I).group().lower()
        self._login(base_host)
        self._set_age_cookies(base_host)
        base = f'https://{domain.lower()}/{kind}/{slug}/videos' + ('' if kind == 'channels' else '/upload')
        maximum, max_pages = self._limit('max_items', 10000), self._limit('max_pages', 500)
        entries, pages, seen, signatures = [], [], set(), set()
        first, total, title = None, None, slug
        for number in range(1, max_pages + 1):
            cards, has_next, count, page_title = self._page(base, number, slug)
            signature = hashlib.sha256('\n'.join(sorted(key for key, _ in cards)).encode()).hexdigest()
            if signature in signatures or (number > 1 and not cards):
                raise ExtractorError('Publication pages repeat or end unexpectedly', expected=True)
            signatures.add(signature)
            if number == 1:
                first, total, title = signature, count, page_title or slug
                if total is not None and total > maximum:
                    raise ExtractorError('Publication list exceeds its item limit', expected=True)
            elif count is not None and total is not None and count != total:
                raise ExtractorError('Publication count changed during enumeration; retry the scan', expected=True)
            added = 0
            for key, video_title in cards:
                if key in seen:
                    continue
                seen.add(key)
                if len(seen) > maximum:
                    raise ExtractorError('Publication list exceeds its item limit', expected=True)
                added += 1
                entries.append(dict(_type='url', ie_key='PornHub', id=key, vd_page=number,
                                    url=f'https://www.{base_host}/view_video.php?viewkey={key}', title=video_title or key))
            if number > 1 and not added:
                raise ExtractorError('Publication page contains no new videos', expected=True)
            pages.append(dict(number=number, count=added, has_next=has_next))
            if not has_next:
                break
            if number == max_pages:
                raise ExtractorError('Publication list exceeds its page limit', expected=True)
            self._sleep(0.25, slug)
        if total is not None and total != len(entries):
            raise ExtractorError('Publication list is incomplete: video count does not match', expected=True)
        # A channel can change while pages are being walked. Do not commit a
        # mixed snapshot if its first page changed in the meantime.
        check, check_next, check_total, _ = self._page(base, 1, slug)
        if hashlib.sha256('\n'.join(sorted(key for key, _ in check)).encode()).hexdigest() != first or check_total != total or check_next != pages[0]['has_next']:
            raise ExtractorError('Publication list changed during enumeration; retry the scan', expected=True)
        result = self.playlist_result(entries, f'{kind}/{slug}', title)
        result['vd_ph_scan'] = dict(version=1, complete=True, source_url=url, listing_url=base,
                                    verified_first_page=True, item_count=len(entries), pages=pages)
        return result
