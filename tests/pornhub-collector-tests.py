"""Offline contract tests against the real yt-dlp API; never contact Pornhub."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
import unittest

from yt_dlp import YoutubeDL
from yt_dlp.utils import ExtractorError

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / 'collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py'
spec = importlib.util.spec_from_file_location('vd_pornhub_test', PLUGIN)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
SOURCE = 'https://rt.pornhub.com/model/fixture-model'
LISTING = SOURCE + '/videos/upload'


def page(keys=('phfixture1',), number=1, next_page=None, total=None, extra='', outside=''):
    count = f' data-total-videos="{total}"' if total is not None else ''
    cards = ''.join(f'<li class="pcVideoListItem" data-video-vkey="{key}"><a href="/view_video.php?viewkey={key}" title="Video {key}">Video</a></li>' for key in keys)
    if not keys:
        cards = '<div class="noVideos">No videos</div>'
    more = '' if next_page is None else f'<li class="page_next"><a href="?page={next_page}">Next</a></li>'
    return f'<html><head></head><body><h1>Fixture channel</h1><ul id="mostRecentVideosSection"{count}>{cards}{extra}</ul><ul class="pagination"><li class="page_current">{number}</li>{more}</ul>{outside}</body></html>'


class Collector(module.VideoDownloaderPornHubChannelIE):
    def __init__(self, pages, **limits):
        self.pages, self.calls, self.failures, self.delays = pages, [], {}, []
        self.authorized_host = None
        params = {'quiet': True, 'no_warnings': True, 'extractor_args': {
            self.ie_key().lower(): {name: [str(value)] for name, value in limits.items()}}}
        super().__init__(YoutubeDL(params))

    def _login(self, host):
        self.authorized_host = host

    def _set_age_cookies(self, host):
        pass

    def _sleep(self, delay, *args, **kwargs):
        self.delays.append(delay)

    def _download_webpage_handle(self, base, channel, *args, query=None, **kwargs):
        number = query['page']
        self.calls.append((base, number))
        response = self.pages[number]
        if callable(response):
            response = response()
        if isinstance(response, Exception):
            raise response
        return response, SimpleNamespace(url=f'{base}?page={number}')


class CollectorTests(unittest.TestCase):
    def test_complete_pages_and_individual_urls(self):
        collector = Collector({1: page(('phfixture1', 'phfixture2'), next_page=2, total=3),
                               2: page(('phfixture3',), number=2, total=3)})
        result = collector.extract(SOURCE)
        self.assertEqual([entry['id'] for entry in result['entries']], ['phfixture1', 'phfixture2', 'phfixture3'])
        self.assertEqual([number for _, number in collector.calls], [1, 2, 1])
        self.assertTrue(all(base == LISTING for base, _ in collector.calls))
        self.assertEqual(result['vd_ph_scan']['pages'], [dict(number=1, count=2, has_next=True), dict(number=2, count=1, has_next=False)])
        self.assertTrue(result['vd_ph_scan']['verified_first_page'])
        self.assertEqual(result['entries'][0]['url'], 'https://www.pornhub.com/view_video.php?viewkey=phfixture1')
        self.assertEqual(collector.authorized_host, 'pornhub.com')

    def test_uploads_not_recommendations_or_watch_history(self):
        unrelated = '<div id="watchHistory"><li class="pcVideoListItem"><a href="/view_video.php?viewkey=phwrong" title="Other">Other</a></li></div>'
        embedded = '<div class="recommendedVideos"><li class="videoBox" data-video-vkey="phwrong2"></li></div>'
        result = Collector({1: page(extra=embedded, outside=unrelated)}).extract(SOURCE)
        self.assertEqual([entry['id'] for entry in result['entries']], ['phfixture1'])

    def test_repeated_page_is_not_a_complete_scan(self):
        with self.assertRaises(ExtractorError):
            Collector({1: page(next_page=2), 2: page(number=2)}).extract(SOURCE)

    def test_missing_advertised_next_page(self):
        with self.assertRaises(ExtractorError):
            Collector({1: page(next_page=2), 2: page((), number=2)}).extract(SOURCE)

    def test_page_error_aborts_whole_snapshot(self):
        with self.assertRaises(ExtractorError):
            Collector({1: page(next_page=2), 2: ExtractorError('HTTP 403', expected=True)}).extract(SOURCE)

    def test_skipped_page_and_foreign_next_link(self):
        for html in (page(next_page=3), page(next_page=2).replace('?page=2', 'https://other.example/videos?page=2')):
            with self.subTest(html=html), self.assertRaises(ExtractorError):
                Collector({1: html}).extract(SOURCE)

    def test_total_must_match(self):
        with self.assertRaises(ExtractorError):
            Collector({1: page(total=9)}).extract(SOURCE)

    def test_empty_channel_requires_a_marker(self):
        result = Collector({1: page((), total=0)}).extract(SOURCE)
        self.assertEqual(result['entries'], [])
        with self.assertRaises(ExtractorError):
            Collector({1: page(()).replace('<div class="noVideos">No videos</div>', '')}).extract(SOURCE)

    def test_no_list_and_ambiguous_lists_fail_closed(self):
        for html in ('<html><body>Please log in</body></html>', page(outside='<ul id="videoSearchResult"></ul>')):
            with self.subTest(html=html), self.assertRaises(ExtractorError):
                Collector({1: html}).extract(SOURCE)

    def test_limits_do_not_return_a_partial_success(self):
        with self.assertRaises(ExtractorError):
            Collector({1: page(('phfixture1', 'phfixture2'))}, max_items=1).extract(SOURCE)
        with self.assertRaises(ExtractorError):
            Collector({1: page(next_page=2)}, max_pages=1).extract(SOURCE)

    def test_changed_first_page_rejects_mixed_snapshot(self):
        responses = iter((page(), page(('phchanged',))))
        with self.assertRaises(ExtractorError):
            Collector({1: lambda: next(responses)}).extract(SOURCE)

    def test_query_does_not_start_in_the_middle_or_change_the_tab(self):
        collector = Collector({1: page()})
        result = collector.extract(SOURCE + '/videos?page=7&o=mv')
        self.assertEqual([number for _, number in collector.calls], [1, 1])
        self.assertEqual(result['vd_ph_scan']['listing_url'], LISTING)

    def test_other_profiles_and_regional_hosts(self):
        for kind in ('model', 'users', 'pornstar', 'channels'):
            source = 'https://www.pornhub.net/' + kind + '/fixture'
            result = Collector({1: page()}).extract(source)
            self.assertEqual(result['vd_ph_scan']['listing_url'], source + '/videos' + ('' if kind == 'channels' else '/upload'))

    def test_localization_redirects_preserve_the_publication_source(self):
        self.assertTrue(module._same_source(LISTING, LISTING.replace('rt.', 'www.')))
        for other in ('https://www.pornhub.com/login', LISTING.replace('fixture-model', 'someone-else'),
                      LISTING.replace('pornhub.com', 'pornhub.com.evil.example'), LISTING.replace('/upload', '/favorites')):
            self.assertFalse(module._same_source(LISTING, other))

    def test_redirect_cannot_start_at_a_later_page_or_drop_pagination(self):
        class Redirected(Collector):
            def _download_webpage_handle(self, base, channel, *args, query=None, **kwargs):
                html, response = super()._download_webpage_handle(base, channel, *args, query=query, **kwargs)
                response.url = base + ('?page=7' if query['page'] == 1 else '')
                return html, response
        with self.assertRaises(ExtractorError):
            Redirected({1: page()}).extract(SOURCE)
        collector = Redirected({1: page(next_page=2), 2: page(('phfixture2',), number=2)})
        # Keep the first response valid and drop the page query only on page two.
        download = collector._download_webpage_handle
        def redirect(base, channel, *args, query=None, **kwargs):
            html, response = download(base, channel, *args, query=query, **kwargs)
            if query['page'] == 1:
                response.url = base + '?page=1'
            return html, response
        collector._download_webpage_handle = redirect
        with self.assertRaises(ExtractorError):
            collector.extract(SOURCE)

    def test_pinned_overlap_is_deduplicated_across_pages(self):
        result = Collector({1: page(('phfixture1', 'phfixture2'), next_page=2, total=3),
                            2: page(('phfixture2', 'phfixture3'), number=2, total=3)}).extract(SOURCE)
        self.assertEqual(len(result['entries']), 3)
        self.assertEqual(result['vd_ph_scan']['pages'][1]['count'], 1)

    def test_truncated_markup_and_unverifiable_load_more_fail(self):
        for html in (page().replace('</body></html>', ''), page(outside='<button id="moreDataBtn">More</button>')):
            with self.subTest(html=html), self.assertRaises(ExtractorError):
                Collector({1: html}).extract(SOURCE)

    def test_plugin_is_discovered_by_real_yt_dlp(self):
        # --list-extractors exits before yt-dlp loads plugins. An intentionally
        # unsupported local scheme exercises actual CLI registration without HTTP.
        result = subprocess.run([sys.executable, '-m', 'yt_dlp', '--ignore-config', '--no-plugin-dirs', '--plugin-dirs',
                                 str(ROOT / 'collection-plugins'), '--use-extractors', 'videodownloader:pornhub:channel',
                                 '--verbose', '--skip-download', 'vdph-test:invalid'], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 1)
        self.assertIn('Extractor Plugins: VideoDownloaderPornHubChannelIE', result.stderr)
        self.assertIn('No suitable extractor found', result.stderr)


if __name__ == '__main__':
    unittest.main()
