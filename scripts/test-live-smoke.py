#!/usr/bin/env python3
"""Offline subprocess tests: python3 scripts/test-live-smoke.py (no live bridge)."""

import ast
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parent.parent
SMOKE = REPO / 'scripts/live-smoke.py'
FAKE = REPO / 'scripts/tests/live_smoke_fake_sts2.py'


class LiveSmokeTests(unittest.TestCase):
    def setUp(self):
        scratch = REPO / '.local'
        scratch.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='test-live-smoke-', dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.shim = self.root / 'sts2'
        self.shim.write_text('#!{}\n'.format(sys.executable) + FAKE.read_text().split('\n', 1)[1])
        self.shim.chmod(0o755)

    def run_smoke(self, scenario='success', fixtures=None, timeout=5, binary=None):
        out = self.root / 'out'
        config = self.root / 'smoke-config.yaml'
        config.touch()
        env = dict(os.environ, SMOKE_FAKE_SCENARIO=scenario,
                   SMOKE_FAKE_STATE=str(self.root / 'fake-state.json'))
        argv = [sys.executable, str(SMOKE), '--sts2', str(binary or self.shim),
                '--config', str(config), '--out', str(out), '--timeout-s', str(timeout)]
        if fixtures is not None:
            argv += ['--fixtures', fixtures]
        result = subprocess.run(argv, cwd=REPO, env=env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.stderr, '', result.stderr)
        report = json.loads(result.stdout)  # Reject extra stdout before/after the single object.
        self.assertEqual(report, json.loads((out / 'summary.json').read_text()))
        self.assertTrue((out / 'summary.md').exists())
        for check in self.checks(report):
            self.assertTrue(Path(check['evidence']).is_file(), check)
        return result.returncode, report

    @staticmethod
    def checks(report):
        return report['preflight'] + [c for f in report['fixtures'] for c in f['checks']]

    def test_all_core_fixtures_and_evidence(self):
        code, report = self.run_smoke()
        self.assertEqual(code, 0)
        self.assertTrue(report['ok'])
        self.assertEqual(report['schema'], 'spirectl-live-smoke/1')
        self.assertEqual(len(report['fixtures']), 5)
        self.assertEqual(report['summary'], {'passed': 26, 'refused': 16, 'failed': 0})
        for fixture in report['fixtures']:
            self.assertEqual(len(fixture['checks']), 8)
        for call in (self.root / 'out').glob('*.call.json'):
            metadata = json.loads(call.read_text())
            self.assertEqual(Path(metadata['stderr']).read_text(), 'fake CLI stderr\n')
            self.assertIsInstance(json.loads(Path(metadata['stdout']).read_text()), dict)

    def test_structured_non_combat_refusals(self):
        code, report = self.run_smoke(fixtures='main-menu')
        self.assertEqual(code, 0)
        refused = [c for c in self.checks(report) if c['outcome'] == 'refused']
        self.assertEqual([c['reasonCode'] for c in refused],
                         ['wrong_screen', 'invalid_action', 'invalid_action', 'wrong_screen'])
        self.assertEqual(report['summary']['refused'], 4)

    def test_committed_combat_cancel_refusal(self):
        code, report = self.run_smoke('cancel_refused', 'combat')
        self.assertEqual(code, 0)
        check = report['fixtures'][0]['checks'][-2]
        self.assertEqual(check['check'], 'end-turn→cancel-end-turn')
        self.assertEqual((check['outcome'], check['reasonCode']), ('refused', 'invalid_action'))

    def test_unexpected_exit_fails_and_continues(self):
        code, report = self.run_smoke('unexpected_exit', 'main-menu,map')
        self.assertEqual(code, 1)
        self.assertFalse(report['ok'])
        self.assertEqual(len(report['fixtures']), 2)
        self.assertEqual(report['fixtures'][0]['checks'][2]['reasonCode'], 'unexpected_exit_code')
        self.assertEqual(report['summary']['failed'], 12)

    def test_mismatch_stops_before_mutations(self):
        code, report = self.run_smoke('mismatch')
        self.assertEqual(code, 2)
        self.assertEqual(report['gameBuild']['status'], 'mismatch')
        self.assertEqual(report['preflight'][0]['reasonCode'], 'game_build_mismatch')
        self.assertEqual(report['fixtures'], [])
        calls = (self.root / 'fake-state.calls').read_text().splitlines()
        self.assertEqual(len(calls), 1)

    def test_unreachable_preflight(self):
        code, report = self.run_smoke('unreachable')
        self.assertEqual(code, 2)
        self.assertEqual(report['preflight'][0]['reasonCode'], 'bridge_unreachable')

    def test_missing_binary_preflight(self):
        code, report = self.run_smoke(binary=self.root / 'missing')
        self.assertEqual(code, 2)
        self.assertEqual(report['preflight'][0]['reasonCode'], 'bridge_unreachable')

    def test_timeout_preflight(self):
        code, report = self.run_smoke('timeout', timeout=0.2)
        self.assertEqual(code, 2)
        self.assertIn('command_timeout', report['preflight'][0]['detail'])

    def test_malformed_json_fails(self):
        code, report = self.run_smoke('bad_json', 'main-menu')
        self.assertEqual(code, 1)
        self.assertEqual(report['fixtures'][0]['checks'][2]['reasonCode'], 'invalid_json')

    def test_malformed_nested_state_is_a_structured_failure(self):
        code, report = self.run_smoke('bad_player_view', 'combat')
        self.assertEqual(code, 1)
        self.assertEqual(report['fixtures'][0]['checks'][2]['reasonCode'], 'invalid_json_shape')

    def test_missing_player_ids_preserves_structured_verdict(self):
        code, report = self.run_smoke('missing_player_ids', 'combat')
        self.assertEqual(code, 1)
        checks = {c['check']: c for c in report['fixtures'][0]['checks']}
        self.assertEqual(checks['play-card']['reasonCode'], 'invalid_json_shape')
        for name in ('end-turn', 'end-turn→cancel-end-turn'):
            self.assertEqual((checks[name]['outcome'], checks[name]['reasonCode']),
                             ('failed', 'dependency_failed'))
        calls = (self.root / 'fake-state.calls').read_text().splitlines()
        self.assertFalse(any(json.loads(c)[:2] == ['act', 'end-turn'] for c in calls))

    def test_turn_uses_action_player_id_without_state_id(self):
        code, report = self.run_smoke('action_player_id_only', 'combat')
        self.assertEqual(code, 1)  # The malformed player still fails play-card validation.
        checks = {c['check']: c for c in report['fixtures'][0]['checks']}
        self.assertEqual(checks['play-card']['reasonCode'], 'invalid_json_shape')
        for name in ('end-turn', 'end-turn→cancel-end-turn'):
            self.assertEqual(checks[name]['outcome'], 'passed')

    def test_shop_waits_for_null_inventory_to_populate(self):
        code, report = self.run_smoke('delayed_inventory', 'shop-inventory')
        self.assertEqual(code, 0)
        self.assertEqual(report['summary'], {'passed': 7, 'refused': 3, 'failed': 0})
        self.assertEqual(report['fixtures'][0]['checks'][-1]['outcome'], 'passed')
        calls = [json.loads(c) for c in (self.root / 'fake-state.calls').read_text().splitlines()]
        self.assertGreaterEqual(calls.count(['state']), 4)

    def test_shop_unavailable_inventory_times_out(self):
        for scenario in ('null_inventory', 'missing_inventory', 'null_shop', 'missing_shop',
                         'non_list_entries'):
            with self.subTest(scenario=scenario):
                code, report = self.run_smoke(scenario, 'shop-inventory', timeout=3)
                self.assertEqual(code, 1)
                check = report['fixtures'][0]['checks'][2]
                self.assertEqual((check['check'], check['outcome'], check['reasonCode']),
                                 ('state', 'failed', 'observation_timeout'))

    def test_unexpected_refusal_fails(self):
        code, report = self.run_smoke('wrong_refusal', 'main-menu')
        self.assertEqual(code, 1)
        self.assertEqual(report['summary']['failed'], 4)
        self.assertTrue(any(c['reasonCode'] == 'unexpected_refusal' for c in self.checks(report)))

    def test_unsupported_fixture_fields_fail(self):
        code, report = self.run_smoke('unsupported', 'main-menu')
        self.assertEqual(code, 1)
        self.assertEqual(report['fixtures'][0]['checks'][0]['outcome'], 'failed')
        self.assertEqual(report['summary']['failed'], 7)

    def test_shop_requires_advertised_ids_and_all_verbs(self):
        for scenario in ('missing_shop_id', 'missing_shop_verb'):
            with self.subTest(scenario=scenario):
                code, report = self.run_smoke(scenario, 'shop-inventory')
                self.assertEqual(code, 1)
                checks = report['fixtures'][0]['checks']
                self.assertEqual(checks[3]['outcome'], 'failed')
                self.assertEqual(checks[-1]['reasonCode'], 'dependency_failed')

    def test_purchase_requires_exact_gold_delta(self):
        code, report = self.run_smoke('wrong_gold', 'shop-inventory', timeout=3)
        self.assertEqual(code, 1)
        self.assertIn(report['fixtures'][0]['checks'][-1]['reasonCode'],
                      ('observation_timeout', 'command_timeout'))

    def test_play_requires_observable_result(self):
        code, report = self.run_smoke('no_play_effect', 'combat', timeout=3)
        self.assertEqual(code, 1)
        self.assertEqual(report['fixtures'][0]['checks'][4]['outcome'], 'failed')

    def test_paths_short_names_and_repeated_fixture_keep_evidence(self):
        code, report = self.run_smoke(fixtures='fixtures/basic-main-menu.sts2.fixture.yaml,basic-main-menu')
        self.assertEqual(code, 0)
        self.assertEqual([f['fixture'] for f in report['fixtures']], ['basic-main-menu'] * 2)
        self.assertNotEqual(report['fixtures'][0]['checks'][0]['evidence'],
                            report['fixtures'][1]['checks'][0]['evidence'])

    def test_python_310_syntax(self):
        for path in (SMOKE, FAKE, Path(__file__)):
            ast.parse(path.read_text(), feature_version=(3, 10))


if __name__ == '__main__':
    unittest.main()
