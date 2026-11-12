"""Poster fallback for explicitly selected Windows preview requests only.

The normal download and subscription extractors remain unchanged. Reuse the
page already fetched by PornHubIE, without a second request or saved HTML.
"""
import re
from html.parser import HTMLParser
from urllib.parse import parse_qs, urljoin, urlsplit

from yt_dlp.extractor.pornhub import PornHubIE

__all__ = ['VideoDownloaderPornHubPreviewIE']

_MAX_PAGE_CHARS = 2 * 1024 * 1024
_MAX_IMAGE_URL_CHARS = 8192
_HOST = re.compile(r'(?:[a-zA-Z0-9-]+\.)?pornhub(?:premium)?\.(?:com|net|org)\Z', re.I)


def _image_url(value, page_url):
    if not value or len(value) > _MAX_IMAGE_URL_CHARS:
        return None
    value = value.strip()
    if not value or any(char.isspace() or ord(char) < 32 or ord(char) == 127 or char == '\\' for char in value):
        return None
    try:
        supplied = urlsplit(value)
        if supplied.scheme and (supplied.scheme not in ('http', 'https') or not supplied.netloc):
            return None
        resolved = urljoin(page_url, value)
        parsed = urlsplit(resolved)
        if (len(resolved) > _MAX_IMAGE_URL_CHARS or parsed.scheme not in ('http', 'https')
                or not parsed.hostname or parsed.username is not None or parsed.password is not None
                or any(char in parsed.hostname for char in '%<>"{}|^`')):
            return None
        # Accessing port validates malformed and out-of-range port numbers.
        parsed.port
        return resolved
    except ValueError:
        return None


class _PosterParser(HTMLParser):
    def __init__(self, page_url):
        super().__init__(convert_charrefs=True)
        self.page_url = page_url
        self.og_image = self.twitter_image = None

    def handle_starttag(self, tag, attrs):
        if tag != 'meta':
            return
        attributes = dict(attrs)
        name = (attributes.get('property') or attributes.get('name') or '').lower()
        if name not in ('og:image', 'twitter:image', 'twitter:image:src'):
            return
        image = _image_url(attributes.get('content'), self.page_url)
        if name == 'og:image':
            self.og_image = self.og_image or image
        else:
            self.twitter_image = self.twitter_image or image


class VideoDownloaderPornHubPreviewIE(PornHubIE):
    IE_NAME = 'videodownloader:pornhub:preview'
    IE_DESC = 'PornHub preview poster fallback'

    def _download_webpage_handle(self, *args, **kwargs):
        result = super()._download_webpage_handle(*args, **kwargs)
        if not result or not getattr(self, '_vd_preview_video_id', None):
            return result
        webpage, response = result
        try:
            page = urlsplit(response.url)
            if (not isinstance(webpage, str) or page.scheme not in ('http', 'https')
                    or not _HOST.fullmatch(page.hostname or '') or page.username is not None
                    or page.password is not None or page.path != '/view_video.php'
                    or parse_qs(page.query).get('viewkey') != [self._vd_preview_video_id]):
                return result
            parser = _PosterParser(response.url)
            parser.feed(webpage[:_MAX_PAGE_CHARS])
            # Retain only the poster URL, never the page. The desktop page wins
            # if the builtin extractor subsequently needs its TV fallback.
            self._vd_preview_poster = self._vd_preview_poster or parser.og_image or parser.twitter_image
        except (ValueError, AssertionError):
            # Optional poster parsing must not replace the builtin result/error.
            pass
        return result

    def _real_extract(self, url):
        self._vd_preview_video_id = self._match_id(url)
        self._vd_preview_poster = None
        try:
            result = super()._real_extract(url)
            if not result.get('thumbnail') and self._vd_preview_poster:
                result['thumbnail'] = self._vd_preview_poster
            return result
        finally:
            self._vd_preview_video_id = self._vd_preview_poster = None
