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

    def test_no_list_and_different_lists_fail_closed(self):
        other = '<ul id="videoSearchResult"><li class="videoblock" data-video-vkey="phwrong"></li></ul>'
        for html in ('<html><body>Please log in</body></html>', page(outside=other)):
            with self.subTest(html=html), self.assertRaises(ExtractorError):
                Collector({1: html}).extract(SOURCE)

    def test_empty_layout_placeholder_does_not_make_the_archive_ambiguous(self):
        result = Collector({1: page(outside='<ul id="videoSearchResult"></ul>')}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phfixture1'])
        for content in ('data-total-videos="0"', 'data-total-videos="8"'):
            with self.subTest(content=content), self.assertRaises(ExtractorError):
                Collector({1: page(outside=f'<ul id="videoSearchResult" {content}></ul>')}).extract(SOURCE)
        for content in ('<a href="/view_video.php?viewkey=phwrong">New card layout</a>',
                        '<button id="moreDataBtn">More</button>'):
            with self.subTest(content=content), self.assertRaises(ExtractorError):
                Collector({1: page(outside=f'<ul id="videoSearchResult">{content}</ul>')}).extract(SOURCE)

    def test_identical_list_copies_are_one_archive_and_walk_all_pages(self):
        def copies(html):
            start = html.index('<ul id="mostRecentVideosSection"')
            end = html.index('</ul>', start) + len('</ul>')
            copy = html[start:end].replace('mostRecentVideosSection', 'videoSearchResult')
            return html[:end] + copy + html[end:]
        result = Collector({1: copies(page(('phfixture1', 'phfixture2'), next_page=2, total=3)),
                            2: copies(page(('phfixture3',), number=2, total=3))}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phfixture1', 'phfixture2', 'phfixture3'])
        self.assertEqual([p['count'] for p in result['vd_ph_scan']['pages']], [2, 1])

    def test_list_copies_with_different_counts_or_subsets_are_rejected(self):
        for other in ('<ul id="videoSearchResult" data-total-videos="2"><li class="videoblock" data-video-vkey="phfixture1"></li></ul>',
                      '<ul id="videoSearchResult"><li class="videoblock" data-video-vkey="phfixture1"></li><li class="videoblock" data-video-vkey="phwrong"></li></ul>'):
            with self.subTest(other=other), self.assertRaises(ExtractorError):
                Collector({1: page(total=1, outside=other)}).extract(SOURCE)

    def test_video_tab_owns_the_archive_and_not_other_page_grids(self):
        def variant(html):
            start = html.index('<ul id="mostRecentVideosSection"')
            end = html.index('<ul class="pagination">', start)
            main = html[start:end].replace('id="mostRecentVideosSection"', 'id="profileGrid" class="videos"')
            # A secondary block has a known ID, its own counter, malformed card
            # and foreign paging; none describe the active publication tab.
            other = ('<div class="videoUList"><ul id="videoSearchResult" data-total-videos="9">'
                     '<li class="videoblock" data-video-vkey="invalid-key"></li>'
                     '<a rel="next" href="https://other.example/?page=8">Next</a>'
                     '<button id="moreDataBtn">More</button></ul></div>')
            tail = html[end:].replace('</body>', '</section>' + other + '</body>')
            return html[:start] + '<section id="videosTab"><div class="videoUList">' + main + '</div>' + tail
        result = Collector({1: variant(page(('phfixture1', 'phfixture2'), next_page=2, total=3)),
                            2: variant(page(('phfixture3',), number=2, total=3))}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phfixture1', 'phfixture2', 'phfixture3'])

    def test_unknown_video_tab_cannot_fall_back_to_a_recognized_list_elsewhere(self):
        for main in ('<ul id="changedGrid"><li class="videoblock" data-video-vkey="phactual"></li></ul>', ''):
            with self.subTest(main=main), self.assertRaises(ExtractorError):
                Collector({1: page(outside=f'<section id="videosTab">{main}</section>')}).extract(SOURCE)

    def test_video_tab_scopes_a_plain_videos_ul_without_a_generic_wrapper(self):
        html = ('<section id="videosTab"><ul class="videos" id="modelMostRecentVideosSection" data-total-videos="1">'
                '<li class="videoblock" data-video-vkey="phactual"></li></ul></section>')
        result = Collector({1: page(outside=html)}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phactual'])

    def test_ancestors_outside_video_tab_cannot_supply_counts_or_paging(self):
        html = ('<div class="videoUList" data-total-videos="9"><section id="videosTab">'
                '<ul class="videos" data-total-videos="1"><li class="videoblock" data-video-vkey="phactual"></li></ul>'
                '</section><a rel="next" href="https://other.example/?page=8">Next</a></div>')
        result = Collector({1: html}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phactual'])

    def test_ownerless_navigation_outside_video_tab_is_not_archive_pagination(self):
        for other in ('<ul class="pagination"><a href="?page=8">Last</a></ul>',
                      '<a rel="next" href="?page=2">Next</a>',
                      '<a rel="next" href="https://other.example/?page=8">Next</a>',
                      '<button id="moreDataBtn">More</button>'):
            with self.subTest(other=other):
                html = page().replace('<body>', '<body><section id="videosTab">').replace('</body>', '</section>' + other + '</body>')
                collector = Collector({1: html})
                result = collector.extract(SOURCE)
                self.assertEqual([e['id'] for e in result['entries']], ['phfixture1'])
                self.assertEqual([n for _, n in collector.calls], [1, 1])

    def test_ownerless_semantic_navigation_inside_video_tab_walks_all_pages(self):
        for tag in ('nav', 'footer'):
            with self.subTest(tag=tag):
                def variant(html):
                    return html.replace('<body>', '<body><section id="videosTab">').replace(
                        '<ul class="pagination">', f'<{tag} class="pagination">').replace(
                        '</ul></body>', f'</{tag}></section></body>')
                collector = Collector({1: variant(page(('phfixture1', 'phfixture2'), next_page=2, total=3)),
                                       2: variant(page(('phfixture3',), number=2, total=3))})
                result = collector.extract(SOURCE)
                self.assertEqual(len(result['entries']), 3)
                self.assertEqual([n for _, n in collector.calls], [1, 2, 1])

    def test_without_a_video_tab_exact_and_generic_grids_remain_ambiguous(self):
        other = ('<div class="videoUList"><ul class="videos" data-total-videos="9">'
                 '<li class="videoblock" data-video-vkey="phwrong"></li>'
                 '<a rel="next" href="https://other.example/?page=8">Next</a></ul></div>')
        with self.assertRaises(ExtractorError):
            Collector({1: page(outside=other)}).extract(SOURCE)

    def test_nested_publication_containers_are_not_independent_lists(self):
        def variant(html):
            return html.replace('<ul id="mostRecentVideosSection"', '<section id="videosTab"><div class="videoUList"><div id="mostRecentVideosSection"><ul class="videos"').replace(
                '</ul><ul class="pagination">', '</ul></div></div></section><ul class="pagination">', 1)
        result = Collector({1: variant(page())}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phfixture1'])

    def test_outer_publication_cards_cannot_be_lost_when_a_nested_list_is_found(self):
        html = ('<div id="mostRecentVideosSection"><li class="videoblock" data-video-vkey="phouter"></li>'
                '<ul id="uploadedVideos"><li class="videoblock" data-video-vkey="phinner"></li></ul></div>')
        with self.assertRaises(ExtractorError):
            Collector({1: html}).extract(SOURCE)

    def test_explicit_wrapper_cannot_merge_independent_nested_lists(self):
        html = ('<div id="mostRecentVideosSection"><ul class="videos">'
                '<li class="videoblock" data-video-vkey="phfirst"></li></ul><ul class="videos">'
                '<li class="videoblock" data-video-vkey="phsecond"></li></ul></div>')
        with self.assertRaises(ExtractorError):
            Collector({1: html}).extract(SOURCE)

    def test_nested_list_count_is_not_lost_under_an_explicit_wrapper(self):
        html = ('<div id="mostRecentVideosSection"><ul class="videos" data-total-videos="9">'
                '<li class="videoblock" data-video-vkey="phactual"></li></ul></div>')
        with self.assertRaisesRegex(ExtractorError, 'count does not match'):
            Collector({1: html}).extract(SOURCE)

    def test_unknown_card_layout_inside_primary_list_is_not_a_placeholder(self):
        html = page(()).replace('<div class="noVideos">No videos</div>', '<a href="/view_video.php?viewkey=phnewlayout">Video</a>')
        with self.assertRaises(ExtractorError):
            Collector({1: html}).extract(SOURCE)

    def test_ambiguous_list_error_reports_names_and_counts_without_video_keys(self):
        other = '<ul id="videoSearchResult"><li class="videoblock" data-video-vkey="phwrong"></li></ul>'
        with self.assertRaises(ExtractorError) as failure:
            Collector({1: page(outside=other)}).extract(SOURCE)
        message = str(failure.exception)
        self.assertIn('#mostRecentVideosSection (1)', message)
        self.assertIn('#videoSearchResult (1)', message)
        self.assertIn(LISTING, message)
        self.assertNotIn('phfixture1', message)
        self.assertNotIn('phwrong', message)

    def test_primary_section_cannot_change_mid_scan_or_during_first_page_recheck(self):
        for change in (lambda html: html.replace('mostRecentVideosSection', 'uploadedVideos'),
                       lambda html: html.replace('<body>', '<body><section id="videosTab">').replace('</body>', '</section></body>')):
            with self.subTest(change=change), self.assertRaisesRegex(ExtractorError, 'section changed'):
                Collector({1: page(next_page=2), 2: change(page(('phfixture2',), number=2))}).extract(SOURCE)
            responses = iter((page(), change(page())))
            with self.subTest(change=change), self.assertRaisesRegex(ExtractorError, 'changed during enumeration'):
                Collector({1: lambda: next(responses)}).extract(SOURCE)

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

    def test_alternative_publication_lists_walk_all_pages(self):
        source = SOURCE.replace('/model/', '/pornstar/')
        for identifier in ('claimedUploadedVideoSection', 'moreData', 'channelVideosList', 'showAllChanelVideos'):
            with self.subTest(identifier=identifier):
                def variant(html):
                    return html.replace('id="mostRecentVideosSection"', f'id="{identifier}" class="videos"').replace('pcVideoListItem', 'videoblock')
                result = Collector({1: variant(page(('phfixture1', 'phfixture2'), next_page=2, total=3)),
                                    2: variant(page(('phfixture3',), number=2, total=3))}).extract(source)
                self.assertEqual(len(result['entries']), 3)
                self.assertEqual(len(result['vd_ph_scan']['pages']), 2)

    def test_class_based_publication_wrapper_excludes_recommendations(self):
        def variant(html):
            return html.replace('<ul id="mostRecentVideosSection"', '<section class="videoUList withHeader"><ul id="profileGrid" class="videos"').replace(
                '</ul><ul class="pagination">', '</ul></section><ul class="pagination">', 1)
        unrelated = '<aside class="recommendedVideos"><div class="videoUList"><li class="videoBox" data-video-vkey="phwrong"></li><a rel="next" href="https://other.example/?page=8">Next</a><button id="moreDataBtn">More</button></div></aside>'
        result = Collector({1: variant(page(('phfixture1', 'phfixture2'), next_page=2, total=3, outside=unrelated)),
                            2: variant(page(('phfixture3',), number=2, total=3, outside=unrelated))}).extract(SOURCE)
        self.assertEqual([e['id'] for e in result['entries']], ['phfixture1', 'phfixture2', 'phfixture3'])

    def test_generic_feed_is_not_a_video_list(self):
        html = page().replace('id="mostRecentVideosSection"', 'id="moreData" class="feedItems"')
        with self.assertRaises(ExtractorError):
            Collector({1: html}).extract(SOURCE)

    def test_semantic_page_navigation_is_followed(self):
        for tag in ('nav', 'footer'):
            with self.subTest(tag=tag):
                def variant(html):
                    return html.replace('<ul class="pagination">', f'<{tag} class="pagination">').replace('</ul></body>', f'</{tag}></body>')
                result = Collector({1: variant(page(('phfixture1', 'phfixture2'), next_page=2, total=3)),
                                    2: variant(page(('phfixture3',), number=2, total=3))}).extract(SOURCE)
                self.assertEqual(len(result['entries']), 3)

    def test_class_wrapper_cannot_merge_two_independent_lists(self):
        html = '<section class="videoUList"><ul class="videos"><li class="videoblock" data-video-vkey="phfixture1"></li></ul><ul class="videos"><li class="videoblock" data-video-vkey="phfixture2"></li></ul></section>'
        with self.assertRaises(ExtractorError):
            Collector({1: html}).extract(SOURCE)

    def test_header_dropdown_grid_is_never_a_publication_archive(self):
        for attributes in ('class="mainMenuDropdown"', 'class="sidebar"', 'id="rightSidebar"',
                           'class="siteFooter"', 'id="header"', 'role="complementary"'):
            with self.subTest(attributes=attributes):
                menu = f'<div {attributes}><div class="videoUList"><ul class="videos"><li class="videoblock" data-video-vkey="phwrong"></li></ul></div></div>'
                result = Collector({1: page(outside=menu)}).extract(SOURCE)
                self.assertEqual([e['id'] for e in result['entries']], ['phfixture1'])
                with self.assertRaises(ExtractorError):
                    Collector({1: menu}).extract(SOURCE)

    def test_unknown_layout_reports_structural_details_without_video_keys(self):
        html = page().replace('mostRecentVideosSection', 'newVideosGrid')
        with self.assertRaises(ExtractorError) as failure:
            Collector({1: html}).extract(SOURCE)
        self.assertIn('#newVideosGrid', str(failure.exception))
        self.assertIn(LISTING, str(failure.exception))
        self.assertNotIn('phfixture1', str(failure.exception))

    def test_age_page_is_an_actionable_failure_never_an_empty_archive(self):
        html = '<html><head><title>Age verification</title></head><body><h1>Verify your age</h1></body></html>'
        with self.assertRaises(ExtractorError) as failure:
            Collector({1: html}).extract(SOURCE)
        self.assertIn('возраста', str(failure.exception))
        self.assertIn('Cookies', str(failure.exception))

    def test_localization_redirects_preserve_the_publication_source(self):
        self.assertTrue(module._same_source(LISTING, LISTING.replace('rt.', 'www.')))
        for other in ('https://www.pornhub.com/login', LISTING.replace('fixture-model', 'someone-else'),
                      LISTING.replace('pornhub.com', 'pornhub.com.evil.example'), LISTING.replace('/upload', '/favorites'),
                      SOURCE, LISTING.replace('/model/', '/users/'), LISTING.replace('rt.', ':password@'),
                      LISTING.replace('rt.', 'www.').replace('.com/', '.com:invalid/')):
            self.assertFalse(module._same_source(LISTING, other))

    def test_publication_alias_redirect_is_used_for_the_rest_of_the_scan(self):
        canonical = SOURCE.replace('rt.', 'www.') + '/videos'
        class Canonical(Collector):
            def _download_webpage_handle(self, base, channel, *args, query=None, **kwargs):
                html, response = super()._download_webpage_handle(base, channel, *args, query=query, **kwargs)
                response.url = canonical + ('?page=' + str(query['page']) if query['page'] != 1 else '')
                # Absolute pagination links may still use the legacy alias.
                html = html.replace('?page=2', LISTING + '?page=2')
                return html, response
        collector = Canonical({1: page(('phfixture1', 'phfixture2'), next_page=2, total=3),
                               2: page(('phfixture3',), number=2, total=3)})
        result = collector.extract(SOURCE)
        self.assertEqual(len(result['entries']), 3)
        self.assertEqual(collector.calls, [(LISTING, 1), (canonical, 2), (canonical, 1)])
        self.assertEqual(result['vd_ph_scan']['listing_url'], LISTING)

    def test_rejected_redirect_reports_destination_without_secrets(self):
        class Login(Collector):
            def _download_webpage_handle(self, base, channel, *args, **kwargs):
                return page(), SimpleNamespace(url='https://user:secret@www.pornhub.com/login?token=secret#secret')
        with self.assertRaises(ExtractorError) as failure:
            Login({1: page()}).extract(SOURCE)
        diagnostic = str(failure.exception)
        self.assertIn('https://www.pornhub.com/login', diagnostic)
        self.assertNotIn('secret', diagnostic)
        self.assertNotIn('user:', diagnostic)

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
