"""Regression checks for event routing, package reuse and evidence boundaries."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = (ROOT / '.github/workflows/ci.yml').read_text()


def job(name):
    match = re.search(r'^  ' + re.escape(name) + r':\n(.*?)(?=^  [\w-]+:|\Z)', WORKFLOW, re.M | re.S)
    if not match:
        raise AssertionError('missing job ' + name)
    return match.group(1)


def enabled(name, event, ref, suite='regular'):
    match = re.search(r'^    if: (.+)$', job(name), re.M)
    if not match:
        return True
    expression = match.group(1).replace('github.event_name', 'event').replace('github.ref', 'ref').replace('inputs.suite', 'suite')
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    return bool(eval(expression, {'__builtins__': {}, 'startsWith': lambda a, b: a.startswith(b), 'always': lambda: True},
                     dict(event=event, ref=ref, suite=suite)))


class CIContractTests(unittest.TestCase):
    def test_routing_regular_performance_and_release(self):
        cases = [('push', 'refs/heads/topic', 'regular', False, False, False),
                 ('push', 'refs/heads/main', 'regular', True, False, False),
                 ('pull_request', 'refs/pull/1/merge', 'regular', True, False, False),
                 ('push', 'refs/tags/other', 'regular', True, False, False),
                 ('push', 'refs/tags/danmuku-v0.0.2', 'regular', True, False, True),
                 ('workflow_dispatch', 'refs/heads/main', 'regular', True, False, False),
                 ('workflow_dispatch', 'refs/heads/main', 'performance', False, True, False),
                 ('workflow_dispatch', 'refs/heads/main', 'all', True, True, False)]
        for event, ref, suite, regular, performance, release in cases:
            for name in ('smoke', 'danmuku-integration'):
                self.assertEqual(regular, enabled(name, event, ref, suite), (name, event, ref, suite))
            for name in ('performance-manifest', 'danmuku-performance', 'performance-summary'):
                self.assertEqual(performance, enabled(name, event, ref, suite), (name, event, ref, suite))
            self.assertEqual(release, enabled('danmuku-release-draft', event, ref, suite))

    def test_downstream_reuses_tested_package_and_runs_both_paths(self):
        for name in ('smoke', 'danmuku-integration', 'danmuku-performance'):
            text = job(name)
            self.assertIn('sha256sum --check', text)
            self.assertNotRegex(text, r'run:.*(?:package\.sh|test-browser\.sh|test-performance\.sh|dotnet build)')
        integration = job('danmuku-integration')
        for milestone in ('m1', 'm2'):
            self.assertIn(f'python3 tests/integration/{milestone}.py --browser\n', integration)
            self.assertIn(f'python3 tests/integration/{milestone}.py --browser --base-url /jellyfin', integration)
        self.assertIn('needs: [standalone, smoke, danmuku-integration]', job('danmuku-release-draft'))

    def test_serial_complete_matrix_and_failure_collection(self):
        groups = json.loads((ROOT / 'tests/integration/performance-groups.json').read_text())
        self.assertEqual(26, len(groups))
        text = job('danmuku-performance')
        self.assertIn('max-parallel: 1', text)
        self.assertIn('fail-fast: false', text)
        self.assertIn('fromJSON(needs.performance-manifest.outputs.matrix)', text)
        self.assertIn('timeout-minutes: 150', text)
        self.assertIn('performance.py --matrix', job('performance-manifest'))
        self.assertIn('performance.py --summarize', job('performance-summary'))
        self.assertIn('if: always()', job('performance-summary'))
        for name in ('danmuku-integration', 'danmuku-performance'):
            for upload in job(name).split('- uses: actions/upload-artifact@v4')[1:]:
                self.assertIn('if: always()', upload)
                paths = re.findall(r'^            (artifacts/\S+)$', upload, re.M)
                self.assertTrue(paths)
                for path in paths:
                    self.assertTrue(path.endswith(('.json', '.log', '.png')))
                    self.assertNotRegex(path, r'credentials|token|/config|/media|/manual|\.db')
                    self.assertNotIn('/**/*', path.replace('/**/*-', '/safe-').replace('/**/*.png', '/safe.png'))


if __name__ == '__main__':
    unittest.main()
