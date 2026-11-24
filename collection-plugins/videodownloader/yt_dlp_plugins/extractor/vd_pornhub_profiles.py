"""Resolve watch-page credits without enumerating or requesting media formats."""
import re
from html.parser import HTMLParser
from urllib.parse import parse_qs, urljoin, urlsplit

from yt_dlp.extractor.pornhub import PornHubBaseIE
from yt_dlp.utils import ExtractorError

__all__ = ['VideoDownloaderPornHubProfilesIE']

_SITE = r'pornhub(?:premium)?\.(?:com|net|org)'
_PROFILE = re.compile(r'/(?:model|pornstar)/[A-Za-z0-9_-]{1,128}(?:/videos(?:/upload)?)?/?\Z')
_VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'}
_MAX_PAGE = 8 * 1024 * 1024


def _page_url(value, site):
    try:
        parsed = urlsplit(value)
        if (parsed.scheme in ('http', 'https') and parsed.username is None
                and parsed.port in (None, 443 if parsed.scheme == 'https' else 80)
                and re.fullmatch(r'(?:[a-z0-9-]+\.)?' + re.escape(site), parsed.hostname or '', re.I)):
            return parsed
    except ValueError:
        pass
    return None


def _profile(value, origin, site):
    if not isinstance(value, str) or not value or any(c.isspace() or c == '\\' for c in value):
        return None
    try:
        page = _page_url(urljoin(origin, value), site)
    except ValueError:
        return None
    if page and _PROFILE.fullmatch(page.path):
        kind, slug = page.path.split('/')[1:3]
        return f'https://www.{site}/{kind}/{slug}'
    return None


class _Credits(HTMLParser):
    _PERFORMERS = {'pornstarswrapper', 'pornstars', 'pstar-list-btn'}
    _UPLOADER = {'usernamewrap', 'video-uploader'}
    _UNRELATED = re.compile(r'recommend|suggested|related.?videos|recently.?viewed|watch.?history', re.I)

    def __init__(self, origin, site):
        super().__init__(convert_charrefs=True)
        self.origin, self.site = origin, site
        self.stack, self.performers, self.uploaders = [], {}, {}
        self.unavailable = False

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        markers = set(' '.join((attrs.get('id') or '', attrs.get('class') or '')).lower().split())
        role = self.stack[-1][1] if self.stack else None
        if role == 'excluded' or tag in {'nav', 'aside', 'footer'} or any(self._UNRELATED.search(m) for m in markers):
            role = 'excluded'
        elif markers & self._PERFORMERS or attrs.get('itemprop') == 'actor':
            role = 'performer'
        elif markers & self._UPLOADER:
            role = 'uploader'
        if role != 'excluded' and (markers & {'geoblocked', 'novideo', 'usermessagesection'}
                                   or tag == 'div' and 'removed' in markers):
            self.unavailable = True
        if tag == 'a' and role in ('performer', 'uploader'):
            link = _profile(attrs.get('href'), self.origin, self.site)
            if link:
                collection = self.performers if role == 'performer' else self.uploaders
                collection[link] = None
        if tag not in _VOID:
            self.stack.append((tag, role))

    def handle_endtag(self, tag):
        for index in range(len(self.stack) - 1, -1, -1):
            if self.stack[index][0] == tag:
                del self.stack[index:]
                break


class VideoDownloaderPornHubProfilesIE(PornHubBaseIE):
    IE_NAME = 'videodownloader:pornhub:profiles'
    IE_DESC = 'Video Downloader: Pornhub watch-page profiles'
    _VALID_URL = rf'https://www\.(?P<site>{_SITE})/view_video\.php\?viewkey=(?P<id>[a-z0-9]{{1,64}})\Z'

    def _real_extract(self, url):
        match = self._match_valid_url(url)
        site, video_id = match.group('site', 'id')
        self._login(site)
        self._set_age_cookies(site)
        self._set_cookie(site, 'platform', 'pc')
        with self._request_webpage(url, video_id, 'Downloading watch page', impersonate=True) as response:
            target = _page_url(response.url, site)
            if (not target or target.path != '/view_video.php'
                    or parse_qs(target.query, keep_blank_values=True).get('viewkey') != [video_id]):
                raise ExtractorError('Pornhub перенаправил запрос. Проверьте доступ к ролику и cookies.', expected=True)
            content = response.read(_MAX_PAGE + 1)
            page_url = response.url
        if len(content) > _MAX_PAGE:
            raise ExtractorError('Страница ролика Pornhub превышает допустимый размер.', expected=True)
        webpage = content.decode('utf-8-sig', errors='replace')
        credits = _Credits(page_url, site)
        credits.feed(webpage)
        credits.close()
        if credits.unavailable:
            raise ExtractorError('Ролик Pornhub недоступен. Проверьте ссылку и cookies.', expected=True)

        candidates = list(credits.performers)
        if not candidates:
            # This is the profile object also read by the builtin PornHubIE.
            owner = self._search_json(r'var\s+MODEL_PROFILE\s*=', webpage, 'model profile', video_id, default={})
            link = _profile(owner.get('modelProfileLink'), page_url, site) if isinstance(owner, dict) else None
            candidates = [link] if link else list(credits.uploaders)
        if not candidates:
            raise ExtractorError('На странице ролика Pornhub не найден профиль модели.', expected=True)
        if len(candidates) > 50:
            raise ExtractorError('На странице Pornhub слишком много ссылок на профили.', expected=True)
        return {**self.playlist_result([], video_id), 'vd_ph_profiles': candidates}
