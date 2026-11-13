#!/usr/bin/env python3
"""Check core fixtures against an already running bridge; stdout is one JSON verdict."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import subprocess
import time

REPO = Path(__file__).resolve().parent.parent
DEFAULT_FIXTURES = ('main-menu', 'map', 'combat', 'lobby', 'shop-inventory')
CHECKS = ('state', 'state-actions', 'play-card', 'end-turn',
          'end-turn→cancel-end-turn', 'buy-card')


class Failure(Exception):
    def __init__(self, code, detail, evidence=None):
        super().__init__(detail)
        self.code = code
        self.evidence = evidence


def require(condition, detail, code='assertion_failed'):
    if not condition:
        raise Failure(code, detail)


def player(state, player_id=None):
    players = state['run']['players']
    player_id = player_id or state['run'].get('view', {}).get('playerId')
    if player_id:
        return next(p for p in players if p['id'] == player_id)
    require(len(players) == 1, 'No unambiguous local player in state')
    return players[0]


def hand(combat):
    return combat['hand']['cards']


def entries(state):
    shop = state['run']['currentRoom'].get('shop') or {}
    inventory = shop.get('inventory') or {}
    return [entry for key in ('characterCardEntries', 'colorlessCardEntries')
            if isinstance(inventory.get(key), list) for entry in inventory[key]]


class Smoke:
    def __init__(self, args):
        self.args = args
        self.out = args.out.resolve()
        self.out.mkdir(parents=True, exist_ok=True)
        self.prefix = [str(args.sts2.resolve()), '--json']
        if args.config:
            self.prefix += ['--config', str(args.config.resolve())]
        self.sequence = 0
        self.evidence = None
        self.report = {'schema': 'spirectl-live-smoke/1', 'ok': False,
                       'gameBuild': None, 'preflight': [], 'fixtures': []}

    def call(self, label, argv, exits=(0,), timeout=None):
        self.sequence += 1
        stem = '{:04d}-{}'.format(self.sequence, re.sub(r'[^a-zA-Z0-9-]', '-', label))
        raw = self.out / (stem + '.json')
        stderr = self.out / (stem + '.stderr')
        self.evidence = str(raw)
        command = self.prefix + argv
        failure = None
        try:
            result = subprocess.run(command, capture_output=True, text=True,
                                    timeout=timeout or self.args.timeout_s, cwd=REPO)
            stdout, err, code = result.stdout, result.stderr, result.returncode
        except subprocess.TimeoutExpired as exc:
            stdout, err, code = exc.stdout or b'', exc.stderr or b'', None
            failure = Failure('command_timeout', '{} timed out'.format(label), str(raw))
        except OSError as exc:
            stdout, err, code = '', str(exc), None
            failure = Failure('command_unavailable', str(exc), str(raw))
        raw.write_text(stdout.decode(errors='replace') if isinstance(stdout, bytes) else stdout,
                       encoding='utf-8')
        stderr.write_text(err.decode(errors='replace') if isinstance(err, bytes) else err,
                          encoding='utf-8')
        (self.out / (stem + '.call.json')).write_text(json.dumps(
            {'argv': command, 'exitCode': code, 'expectedExitCodes': list(exits),
             'stdout': str(raw), 'stderr': str(stderr)}, indent=2) + '\n', encoding='utf-8')
        if failure:
            raise failure
        require(code in exits, '{} exited {}; expected {}'.format(label, code, exits),
                'unexpected_exit_code')
        try:
            data = json.loads(stdout)
        except ValueError as exc:
            raise Failure('invalid_json', '{}: {}'.format(label, exc), str(raw)) from exc
        require(isinstance(data, dict), '{} returned a non-object JSON value'.format(label),
                'invalid_json_shape')
        if code == 0:
            require(not data.get('error'), '{} returned an error with exit 0'.format(label))
        return code, data

    def check(self, checks, name, operation):
        self.evidence = None
        try:
            outcome, reason, detail = operation()
            row = {'check': name, 'outcome': outcome, 'reasonCode': reason, 'detail': detail}
        except Failure as exc:
            row = {'check': name, 'outcome': 'failed', 'reasonCode': exc.code, 'detail': str(exc)}
            if exc.evidence:
                self.evidence = exc.evidence
        except (AttributeError, KeyError, TypeError, ValueError, IndexError, StopIteration) as exc:
            row = {'check': name, 'outcome': 'failed', 'reasonCode': 'invalid_json_shape',
                   'detail': '{}: {}'.format(type(exc).__name__, exc)}
        if self.evidence is None:
            self.sequence += 1
            path = self.out / ('{:04d}-dependency.check.json'.format(self.sequence))
            path.write_text(json.dumps(row, indent=2) + '\n', encoding='utf-8')
            self.evidence = str(path)
        row['evidence'] = self.evidence
        checks.append(row)
        return row['outcome'] != 'failed'

    def poll(self, label, argv, predicate):
        deadline = time.monotonic() + self.args.timeout_s
        answered = False
        while True:
            require(time.monotonic() < deadline, '{} did not become ready'.format(label),
                    'observation_timeout')
            remaining = deadline - time.monotonic()
            try:
                _, data = self.call(label, argv, timeout=max(0.001, remaining))
            except Failure as exc:
                # Each poll command gets only the remaining window. Once an earlier poll answered,
                # a cut-off command means the observation ran out; a first command that never
                # answered is a hung command.
                if exc.code == 'command_timeout' and answered:
                    raise Failure('observation_timeout', '{} did not become ready'.format(label),
                                  exc.evidence) from exc
                raise
            answered = True
            if predicate(data):
                return data
            require(time.monotonic() < deadline, '{} did not become ready'.format(label),
                    'observation_timeout')
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))

    def action(self, label, argv, refusal=None, allow_refusal=False):
        code, data = self.call(label, ['act'] + argv, (0, 3) if allow_refusal else
                               ((3,) if refusal else (0,)))
        if code == 3:
            reason = data['error']['actionFailure']['reasonCode']
            require(reason == refusal, 'Expected refusal {}; got {}'.format(refusal, reason),
                    'unexpected_refusal')
            return 'refused', reason, 'Structured action refusal (exit 3)'
        require(data.get('accepted') is True, '{} was not accepted'.format(label),
                'action_not_accepted')
        return 'passed', None, 'Action accepted'

    def health(self):
        # Build mismatches use a nonzero CLI exit, so inspect the payload first.
        code, payload = self.call('bridge-health', ['game', 'bridge-health', '--verbose'], (0, 4))
        data = payload.get('health', payload)
        build = data['compatibility']['gameBuild']
        self.report['gameBuild'] = build
        if build['status'] == 'mismatch':
            raise Failure('game_build_mismatch', 'Expected gameBuild.status == match')
        require(code == 0 and data.get('status') == 'reachable_current',
                'Bridge is not reachable/current (exit {})'.format(code), 'bridge_unreachable')
        require(build['status'] == 'match', 'Expected gameBuild.status == match',
                'game_build_mismatch')
        return 'passed', None, 'Running bridge and configured game build match'

    def tutorials(self):
        self.call('tutorials', ['dev', 'tutorials', '--disable'])
        return 'passed', None, 'Tutorials disabled for the active profile'

    def fixture(self, path):
        name = path.name.removesuffix('.sts2.fixture.yaml')
        screen = name.removeprefix('basic-')
        checks = []
        self.report['fixtures'].append({'fixture': name, 'checks': checks})

        def load():
            require(screen in DEFAULT_FIXTURES, 'Unknown core fixture: {}'.format(path),
                    'unsupported_fixture')
            _, data = self.call(name + '-load', ['dev', 'fixture', 'load', '--path', str(path)])
            recipe = data['recipeReport']
            require(recipe['bridgeValidation']['status'] == 'passed', 'Bridge validation failed')
            require(recipe['unsupportedFields'] == [], 'Fixture has unsupported fields')
            return 'passed', None, 'Fixture bridge validation passed; no unsupported fields'

        def wait():
            self.call(name + '-settled', ['dev', 'wait-for-transitions', '--timeout-ms',
                      str(max(1, int(self.args.timeout_s * 1000))), '--stable-samples', '5',
                      '--interval-ms', '100'])
            return 'passed', None, 'Transitions settled'

        if not self.check(checks, 'fixture-load', load) or not self.check(checks, 'wait-for-transitions', wait):
            self.blocked(checks, CHECKS)
            return
        context = {}

        def state_check():
            def ready(state):
                require(state.get('blockingModal') is None, 'A modal blocks the screen')
                if screen == 'main-menu':
                    return state['rootScene'] == 'screens/main_menu'
                if screen == 'map':
                    return state['run']['map']['view']['isOpen'] is True
                if screen == 'lobby':
                    return state['characterSelect']['lobby']['maxPlayers'] == 4
                if screen == 'combat':
                    return bool(state['run']['currentRoom']['combat']) and len(hand(player(state)['combat'])) == 5
                shop = state['run']['currentRoom'].get('shop') or {}
                return (state['run']['currentRoom']['scene'] == 'rooms/merchant_room'
                        and (shop.get('view') or {}).get('isOpen') is True and bool(entries(state)))
            context['state'] = self.poll(name + '-state', ['state'], ready)
            return 'passed', None, 'Expected {} screen is ready'.format(screen)

        if not self.check(checks, 'state', state_check):
            self.blocked(checks, CHECKS[1:])
            return

        def actions_check():
            def ready(data):
                actions = data['actions']
                require(isinstance(actions, list), 'actions must be an array')
                require(all(isinstance(a, dict) and isinstance(a.get('kind'), str)
                            and isinstance(a.get('enabled'), bool)
                            and isinstance(a.get('args'), dict) for a in actions),
                        'Each action must include kind, enabled and args')
                if screen == 'combat':
                    return any(a['kind'] == 'play-card' and a['enabled'] is True
                               and a['args'].get('targetId') for a in actions)
                if screen == 'shop-inventory':
                    for kind in ('buy-card', 'buy-relic', 'buy-potion', 'remove-card'):
                        matches = [a for a in actions if a['kind'] == kind]
                        require(matches, '{} is missing from shop actions'.format(kind))
                        require(all(a['args'].get('shopItemId') for a in matches),
                                '{} has no shopItemId'.format(kind))
                return True
            context['actions'] = self.poll(name + '-actions', ['state', 'actions'], ready)['actions']
            return 'passed', None, 'Screen actions are structured and expected verbs are listed'

        actions_ok = self.check(checks, 'state-actions', actions_check)

        def play():
            if screen != 'combat':
                return self.action(name + '-play-card', ['play-card', '--card', 'absent-card'], 'wrong_screen')
            require(actions_ok, 'state-actions failed', 'dependency_failed')
            action = next(a['args'] for a in context['actions'] if a['kind'] == 'play-card'
                          and a['enabled'] is True and a['args'].get('targetId'))
            context['playerId'] = action['playerId']
            # Refresh state after action discovery so the baseline belongs to this player.
            _, before = self.call(name + '-before-play', ['state'])
            energy = player(before, action['playerId'])['combat']['energy']
            enemies = before['run']['currentRoom']['combat']['combatState']['enemies']
            hp = next(e['currentHp'] for e in enemies if e['id'] == action['targetId'])
            self.action(name + '-play-card', ['play-card', '--card', action['cardId'],
                        '--target', action['targetId'], '--player-id', action['playerId']])

            def changed(state):
                combat = player(state, action['playerId'])['combat']
                enemies = state['run']['currentRoom']['combat']['combatState']['enemies']
                after_hp = next((e['currentHp'] for e in enemies if e['id'] == action['targetId']), 0)
                return (all(c['id'] != action['cardId'] for c in hand(combat))
                        and combat['energy'] < energy and after_hp < hp)
            self.poll(name + '-after-play', ['state'], changed)
            return 'passed', None, 'Played card left hand; energy and targeted enemy HP fell'

        self.check(checks, 'play-card', play)

        def end():
            turn_args = []
            if screen == 'combat':
                player_id = context.get('playerId') or player(context['state']).get('id')
                require(player_id, 'No player ID available for end-turn', 'dependency_failed')
                turn_args = ['--player-id', player_id]
            context['turn_args'] = turn_args
            return self.action(name + '-end-turn', ['end-turn'] + turn_args,
                               None if screen == 'combat' else 'invalid_action')
        end_ok = self.check(checks, 'end-turn', end)

        def cancel():
            require(end_ok, 'end-turn failed', 'dependency_failed')
            # A single seat may already have committed its turn; invalid_action is expected then.
            return self.action(name + '-cancel-end-turn', ['cancel-end-turn'] + context['turn_args'],
                               'invalid_action', allow_refusal=screen == 'combat')
        self.check(checks, 'end-turn→cancel-end-turn', cancel)

        def buy():
            if screen != 'shop-inventory':
                return self.action(name + '-buy-card', ['buy-card', '--shop-item', 'absent-shop-item'], 'wrong_screen')
            require(actions_ok, 'state-actions failed', 'dependency_failed')
            state = context['state']
            local = player(state)
            action = next(a for a in context['actions'] if a['kind'] == 'buy-card' and a['enabled'] is True
                          and any(e['id'] == a['args']['shopItemId'] and e.get('card')
                                  and e['cost'] <= local['gold'] for e in entries(state)))
            item_id = action['args']['shopItemId']
            entry = next(e for e in entries(state) if e['id'] == item_id)
            self.action(name + '-buy-card', ['buy-card', '--shop-item', item_id])

            def changed(after):
                slot = next((e for e in entries(after) if e['id'] == item_id), None)
                return ((slot is None or slot.get('card') is None)
                        and player(after, local['id'])['gold'] == local['gold'] - entry['cost'])
            self.poll(name + '-after-buy', ['state'], changed)
            return 'passed', None, 'Advertised shop item consumed; gold fell by its cost'
        self.check(checks, 'buy-card', buy)

    def blocked(self, checks, names):
        for name in names:
            checks.append({'check': name, 'outcome': 'failed', 'reasonCode': 'dependency_failed',
                           'detail': 'Fixture setup/state failed; check could not run',
                           'evidence': self.evidence})

    def finish(self, preflight_failed=False):
        all_checks = self.report['preflight'] + [c for f in self.report['fixtures'] for c in f['checks']]
        counts = Counter(c['outcome'] for c in all_checks)
        self.report['summary'] = {key: counts[key] for key in ('passed', 'refused', 'failed')}
        self.report['ok'] = counts['failed'] == 0
        serialized = json.dumps(self.report, indent=2, ensure_ascii=False) + '\n'
        (self.out / 'summary.json').write_text(serialized, encoding='utf-8')
        lines = ['# Live smoke', '', 'ok: {}'.format(self.report['ok']), '',
                 '| Fixture | Check | Outcome | Reason | Detail | Evidence |',
                 '| --- | --- | --- | --- | --- | --- |']
        groups = [{'fixture': 'preflight', 'checks': self.report['preflight']}] + self.report['fixtures']
        for group in groups:
            for check in group['checks']:
                cells = [group['fixture'], check['check'], check['outcome'], check['reasonCode'],
                         check['detail'], check['evidence']]
                lines.append('| ' + ' | '.join(str(c or '').replace('|', '\\|').replace('\n', ' ')
                                               for c in cells) + ' |')
        (self.out / 'summary.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        print(serialized, end='')
        return 2 if preflight_failed else (1 if counts['failed'] else 0)

    def run(self):
        if not self.check(self.report['preflight'], 'bridge-health', self.health):
            row = self.report['preflight'][-1]
            if row['reasonCode'] not in ('game_build_mismatch', 'bridge_unreachable'):
                row['detail'] = '{}: {}'.format(row['reasonCode'], row['detail'])
                row['reasonCode'] = 'bridge_unreachable'
            return self.finish(preflight_failed=True)
        if not self.check(self.report['preflight'], 'tutorials', self.tutorials):
            return self.finish(preflight_failed=True)
        for name in self.args.fixtures.split(','):
            path = Path(name.strip())
            if path.suffix != '.yaml':
                short = path.name.removeprefix('basic-')
                path = REPO / 'fixtures' / ('basic-' + short + '.sts2.fixture.yaml')
            self.fixture(path.resolve())
        return self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sts2', type=Path, default=Path('./target/debug/sts2'))
    parser.add_argument('--config', type=Path, help='Pass this config to every sts2 invocation')
    parser.add_argument('--fixtures', default=','.join(DEFAULT_FIXTURES),
                        help='Comma-separated core fixture short names or YAML paths')
    parser.add_argument('--out', type=Path, default=Path('.local/live-smoke') /
                        datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ'))
    parser.add_argument('--timeout-s', type=float, default=120,
                        help='Timeout per command/readiness poll (default: 120)')
    args = parser.parse_args()
    if args.timeout_s <= 0 or not math.isfinite(args.timeout_s):
        parser.error('--timeout-s must be finite and positive')
    if not args.fixtures.strip() or any(not name.strip() for name in args.fixtures.split(',')):
        parser.error('--fixtures must contain nonempty fixture names')
    return Smoke(args).run()


if __name__ == '__main__':
    raise SystemExit(main())
