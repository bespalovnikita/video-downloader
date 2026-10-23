"""Complete, uploads-only discovery. Media downloads use yt-dlp's PornHubIE.

Loaded explicitly by Video Downloader's subscription scanner, never installed
into yt-dlp's automatic plugin directory. Requires no external Python runtime.
"""

import hashlib
import re
from html.parser import HTMLParser
from urllib.parse import parse_qs, urljoin, urlsplit, urlunsplit

from yt_dlp.extractor.pornhub import PornHubBaseIE
from yt_dlp.utils import ExtractorError

__all__ = ['VideoDownloaderPornHubChannelIE']

_HOST = r'(?:[a-zA-Z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)'
_PATH = r'/(?P<kind>channels|model|pornstar|users)/(?P<slug>[A-Za-z0-9_-]+)(?:/videos(?:/upload)?)?/?'
_VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'}
# Known publication selectors also used by RSS-Bridge/RSSHub and JDownloader.
# Never fall back to all watch links from the document.
_LISTS = {'videoSearchResult', 'mostRecentVideosSection', 'pornstarsVideoSection', 'uploadedVideos',
          'claimedUploadedVideoSection', 'channelVideosList', 'showAllChanelVideos'}
_CARDS = {'pcVideoListItem', 'videoBox', 'videoblock'}
_EXCLUDED = re.compile(r'watch.?history|recently.?viewed|recommended|suggested|related.?videos|favorites|favourites|drop.?down|menu|side.?bar', re.I)
_CHROME = re.compile(r'(?:^|[\s_-])(?:(?:site|page)[_-]?)?(?:header|footer)(?:$|[\s_-])', re.I)


def _same_source(origin, target):
    """Accept publication endpoint aliases, keeping the site and profile fixed."""
    try:
        left, right = urlsplit(origin), urlsplit(target)
        site = re.search(r'pornhub(?:premium)?\.(?:com|net|org)$', left.hostname or '', re.I)
        if (site is None or right.scheme != 'https' or right.username is not None
                or right.port not in (None, 443)
                or re.fullmatch(r'(?:[a-z0-9-]+\.)?' + re.escape(site.group()), right.hostname or '', re.I) is None):
            return False
        route = r'/(channels|model|pornstar|users)/([A-Za-z0-9_-]+)/videos(/upload)?/?'
        source, destination = re.fullmatch(route, left.path), re.fullmatch(route, right.path)
        return (source is not None and destination is not None
                and source.group(1, 2) == destination.group(1, 2)
                and (source[1] != 'channels' or not destination[3]))
    except ValueError:
        return False


def _diagnostic_url(url):
    """Show the redirect destination without credentials or arbitrary query data."""
    try:
        parsed = urlsplit(url)
        host = parsed.hostname or ''
        if parsed.port is not None:
            host += f':{parsed.port}'
        page = parse_qs(parsed.query).get('page', [])
        query = 'page=' + page[0] if len(page) == 1 and re.fullmatch(r'[0-9]+', page[0]) else ''
        return re.sub(r'[\x00-\x1f\x7f]', '', urlunsplit((parsed.scheme, host, parsed.path, query, '')))[:300]
    except ValueError:
        return '(некорректный URL)'


class _Page(HTMLParser):
    """Only read video cards inside a known publication list, never the page footer."""

    def __init__(self, html, listing_url, page):
        super().__init__(convert_charrefs=True)
        self.listing_url, self.page = listing_url, page
        self.stack, self.lists, self.navigation = [], [], []
        self.video_tabs = 0
        self.card, self.title_parts, self.document_title = None, [], []
        self.candidates = []
        self.outside_cards = 0
        self.feed(html)
        self.close()
        if self.stack or self.card is not None:
            raise ExtractorError('Incomplete publication HTML (unclosed elements)', expected=True)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        classes = set((attrs.get('class') or '').split())
        parent = self.stack[-1] if self.stack else {}
        # Profile layouts also use videoUList wrappers and moreData video lists.
        # moreData.feedItems alone is a general activity feed, not publications.
        explicit = attrs.get('id') in _LISTS or (tag == 'ul' and attrs.get('id') == 'moreData' and 'videos' in classes)
        publication = explicit or (tag in ('div', 'section') and 'videoUList' in classes)
        marker = (attrs.get('id') or '') + ' ' + ' '.join(classes)
        unrelated = bool(_EXCLUDED.search(marker))
        auxiliary = (tag in ('nav', 'header', 'aside', 'footer') or bool(_CHROME.search(marker))
                     or (attrs.get('role') or '').lower() in ('navigation', 'complementary', 'banner', 'contentinfo'))
        excluded = parent.get('excluded', False) or auxiliary or unrelated
        # Main navigation can be a <nav> or <footer>; excluding its cards must
        # not hide advertised pages. Explicitly unrelated paging is ignored.
        pagination_excluded = parent.get('pagination_excluded', False) or unrelated
        concrete = explicit or (tag == 'ul' and 'videos' in classes
                                and (parent.get('listing') is not None or parent.get('video_tab', False)))
        scoped = publication or concrete or parent.get('listing') is not None
        pagination = parent.get('pagination', False) or bool(classes & {'pagination', 'page_next', 'page_current', 'page_last'})
        disabled = parent.get('disabled', False) or 'disabled' in classes
        video_tab = parent.get('video_tab', False) or (tag == 'section' and attrs.get('id') == 'videosTab')
        if tag == 'section' and attrs.get('id') == 'videosTab' and not excluded:
            self.video_tabs += 1
        node = dict(tag=tag, excluded=excluded, pagination=pagination, disabled=disabled,
                    next=parent.get('next', False) or 'page_next' in classes, title=tag == 'h1' or parent.get('title', False),
                    document_title=tag == 'title' or parent.get('document_title', False),
                    listing=parent.get('listing'), video_tab=video_tab, pagination_excluded=pagination_excluded)
        if (publication or concrete) and not excluded:
            name = '#' + attrs['id'] if explicit else ('ul.videos' if concrete else tag + '.videoUList')
            node['listing'] = dict(name=name, explicit=explicit, video_tab=video_tab, parent=parent.get('listing'),
                                   cards=[], totals=[], empty=False, errors=[], unknown_links=0)
            self.lists.append(node['listing'])
            for key in ('data-total-videos', 'data-total-count', 'data-videos-count'):
                if key in attrs:
                    node['listing']['totals'].append(attrs[key])
        if scoped and not excluded:
            if classes & {'noVideos', 'noVideosAvailable', 'noVideosMsg', 'noResults'}:
                node['listing']['empty'] = True
            if classes & _CARDS and self.card is None:
                self.card = dict(depth=len(self.stack), ids=set(), title='', listing=node['listing'])
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
                            self._card_error('Publication card points outside its source')
                        else:
                            self._add_key(values[0])
                        self.card['title'] = self.card['title'] or (attrs.get('title') or '').strip()
            elif tag == 'a' and urlsplit(urljoin(self.listing_url, attrs.get('href') or '')).path == '/view_video.php':
                # A changed card layout must not look like an empty placeholder.
                for listing in self._ancestors(node['listing']):
                    listing['unknown_links'] += 1
        elif classes & _CARDS and not excluded:
            self.outside_cards += 1
        identifier = attrs.get('id') or ''
        if (tag in ('ul', 'div', 'section') and not excluded and len(self.candidates) < 6
                and re.fullmatch(r'[A-Za-z][A-Za-z0-9_-]{0,59}', identifier)
                and re.search(r'video|upload|moreData', identifier, re.I) and identifier not in self.candidates):
            self.candidates.append(identifier)
        if attrs.get('id') == 'moreDataBtn' and not disabled and not pagination_excluded:
            self.navigation.append((node['listing'], 'more', True))
        relation = (attrs.get('rel') or '').split()
        if tag in ('a', 'link') and attrs.get('href'):
            href = attrs['href']
            is_next = 'next' in relation or node['next']
            if (pagination or is_next) and not disabled and not pagination_excluded:
                target = urlsplit(urljoin(self.listing_url, href))
                if not _same_source(self.listing_url, target.geturl()):
                    if is_next:
                        self.navigation.append((node['listing'], 'error', 'Next page changes the publication source'))
                else:
                    values = parse_qs(target.query).get('page', ['1'])
                    if len(values) != 1 or not re.fullmatch(r'[1-9]\d*', values[0]):
                        self.navigation.append((node['listing'], 'error', 'Invalid pagination index'))
                    else:
                        number = int(values[0])
                        self.navigation.append((node['listing'], 'next' if is_next else 'page', number))
        if tag not in _VOID:
            self.stack.append(node)

    def handle_startendtag(self, tag, attributes):
        self.handle_starttag(tag, attributes)
        if tag not in _VOID:
            self.handle_endtag(tag)

    def _add_key(self, key):
        if not re.fullmatch(r'[a-z0-9]+', key):
            self._card_error('Invalid publication video key')
        else:
            self.card['ids'].add(key)

    @staticmethod
    def _ancestors(listing):
        while listing is not None:
            yield listing
            listing = listing['parent']

    def _card_error(self, message):
        for listing in self._ancestors(self.card['listing']):
            listing['errors'].append(message)

    def handle_endtag(self, tag):
        index = next((i for i in range(len(self.stack) - 1, -1, -1) if self.stack[i]['tag'] == tag), None)
        if index is None:
            return
        del self.stack[index:]
        if self.card is not None and len(self.stack) <= self.card['depth']:
            if len(self.card['ids']) != 1:
                self._card_error('A publication card has no unique video URL')
            else:
                for listing in self._ancestors(self.card['listing']):
                    listing['cards'].append((next(iter(self.card['ids'])), self.card['title']))
            self.card = None

    def handle_data(self, data):
        if self.stack and self.stack[-1].get('title'):
            self.title_parts.append(data)
        if self.stack and self.stack[-1].get('document_title'):
            self.document_title.append(data)

    def result(self):
        # videosTab is the profile's publication boundary. A recognized carousel
        # elsewhere cannot stand in for an unknown/empty layout inside the tab.
        candidates = [listing for listing in self.lists if not self.video_tabs or listing['video_tab']]
        if not candidates:
            title = ' '.join(''.join(self.document_title + self.title_parts).split())
            age = re.search(r'age.?verif|verify your age|confirm your age|over 18|старше.?18|подтвер\w* возраст', title, re.I)
            reason = 'Получена страница подтверждения возраста.' if age else 'Не распознан список публикаций на полученной странице.'
            detail = ', '.join('#' + name for name in self.candidates) or 'нет'
            raise ExtractorError(
                f'{reason} Адрес: {_diagnostic_url(self.listing_url)}. '
                f'Секции видео: {detail}; карточек вне списка: {self.outside_cards}. '
                'Если сайт требует подтверждение возраста или вход, укажите файл сессии браузера в поле Cookies основного окна.', expected=True)
        # An outer wrapper and its actual UL are one list, not competing lists.
        # Without an authoritative tab, do not guess between independent grids
        # by document order, card count or specificity of their selectors.
        pool = candidates
        containers = {id(parent) for listing in pool for parent in self._ancestors(listing['parent'])}
        selected = [listing for listing in pool if id(listing) not in containers]
        # Bare empty ULs are layout placeholders. A count, empty-state marker,
        # watch link, malformed card or paging makes a list meaningful instead.
        meaningful = [listing for listing in selected if listing['cards'] or listing['totals'] or listing['empty']
                      or listing['errors'] or listing['unknown_links']
                      or any(owner is listing for owner, _, _ in self.navigation)]
        selected = meaningful or selected
        for listing in self.lists:
            listing['keys'] = frozenset(key for key, _ in listing['cards'])
        unique = dict(selected[0]['cards'])
        keys = frozenset(unique)
        if any(listing['keys'] != keys for listing in selected):
            self._ambiguous(selected)
        # Do not silently drop cards that belong directly to a publication ID
        # (or generic wrapper) when narrower lists were found inside it.
        selected_ids = {id(listing) for listing in selected}
        for listing in pool:
            if id(listing) not in selected_ids and listing['keys'] - keys:
                self._ambiguous(selected + [listing])
        for listing in pool:
            if listing['errors'] or listing['unknown_links']:
                raise ExtractorError((listing['errors'] or ['Unrecognized publication card layout'])[0], expected=True)
        owners = {id(parent) for listing in selected for parent in self._ancestors(listing)
                  if not self.video_tabs or parent['video_tab']}
        counts = []
        for listing in self.lists:
            if id(listing) not in owners or listing['keys'] != keys:
                continue
            for value in listing['totals']:
                if not value or not re.fullmatch(r'\d+', value):
                    raise ExtractorError('Invalid publication count', expected=True)
                counts.append(int(value))
        if len(set(counts)) > 1:
            raise ExtractorError('Publication counts disagree', expected=True)
        total = counts[0] if counts else None
        empty = any(listing['empty'] for listing in self.lists if id(listing) in owners and listing['keys'] == keys)
        if not unique and not (empty or total == 0):
            raise ExtractorError('Empty publication page has no confirmed empty-state marker', expected=True)
        navigation = [(kind, value) for owner, kind, value in self.navigation if owner is None or id(owner) in owners]
        for kind, value in navigation:
            if kind == 'error':
                raise ExtractorError(value, expected=True)
        next_links = [value for kind, value in navigation if kind == 'next']
        if len(set(next_links)) > 1 or any(number != self.page + 1 for number in next_links):
            raise ExtractorError('Pagination skips or repeats a page', expected=True)
        has_next = bool(next_links)
        if not has_next and any(kind == 'more' or (kind == 'page' and value > self.page) for kind, value in navigation):
            raise ExtractorError('Pagination ended before its last advertised page', expected=True)
        if has_next and not unique:
            raise ExtractorError('Empty page advertises more publications', expected=True)
        identity = (bool(self.video_tabs), tuple(sorted({next(
            (parent['name'] for parent in self._ancestors(listing) if parent['explicit'] and id(parent) in owners),
            listing['name']) for listing in selected})))
        return list(unique.items()), has_next, total, ' '.join(''.join(self.title_parts).split()), identity

    def _ambiguous(self, listings):
        detail = ', '.join(f'{listing["name"]} ({len(dict(listing["cards"]))})' for listing in listings[:6])
        raise ExtractorError(
            'Не удалось однозначно выбрать список публикаций. '
            f'Адрес: {_diagnostic_url(self.listing_url)}. Секции (число видео): {detail}. '
            'Разные списки не объединены; прежние результаты сохранены.', expected=True)


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
                    raise ExtractorError(
                        'Переход со страницы публикаций отклонён: '
                        f'{_diagnostic_url(base)} → {_diagnostic_url(response.url)}. '
                        'Ожидалась вкладка видео того же профиля; проверьте адрес и доступ к сайту.', expected=True)
                actual_page = parse_qs(urlsplit(response.url).query, keep_blank_values=True).get('page', ['1'])
                if actual_page != [str(number)]:
                    raise ExtractorError(
                        f'Сайт изменил номер страницы публикаций: ожидалась {number}, '
                        f'получено {_diagnostic_url(response.url)}.', expected=True)
                if len(html.encode('utf-8')) > 8 * 1024 * 1024:
                    raise ExtractorError('Publication HTML exceeds 8 MiB', expected=True)
                resolved = urlsplit(response.url)
                endpoint = urlunsplit((resolved.scheme, resolved.netloc, resolved.path.rstrip('/'), '', ''))
                return (*_Page(html, endpoint, number).result(), endpoint)
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
        first, total, title, endpoint, selection = None, None, slug, base, None
        for number in range(1, max_pages + 1):
            cards, has_next, count, page_title, identity, endpoint = self._page(endpoint, number, slug)
            signature = hashlib.sha256('\n'.join(sorted(key for key, _ in cards)).encode()).hexdigest()
            if signature in signatures or (number > 1 and not cards):
                raise ExtractorError('Publication pages repeat or end unexpectedly', expected=True)
            signatures.add(signature)
            if number == 1:
                first, total, title, selection = signature, count, page_title or slug, identity
                if total is not None and total > maximum:
                    raise ExtractorError('Publication list exceeds its item limit', expected=True)
            elif identity != selection:
                raise ExtractorError('Publication section changed during enumeration; retry the scan', expected=True)
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
        check, check_next, check_total, _, check_selection, _ = self._page(endpoint, 1, slug)
        if hashlib.sha256('\n'.join(sorted(key for key, _ in check)).encode()).hexdigest() != first or check_total != total or check_next != pages[0]['has_next'] or check_selection != selection:
            raise ExtractorError('Publication list changed during enumeration; retry the scan', expected=True)
        result = self.playlist_result(entries, f'{kind}/{slug}', title)
        result['vd_ph_scan'] = dict(version=1, complete=True, source_url=url, listing_url=base,
                                    verified_first_page=True, item_count=len(entries), pages=pages)
        return result
