#!/usr/bin/env python3
"""Synthetic CLI subprocess used only by the offline live-smoke tests."""

import json
import os
from pathlib import Path
import sys
import time

args = sys.argv[1:]
assert args.pop(0) == '--json'
if args[0] == '--config':
    assert Path(args[1]).name == 'smoke-config.yaml'
    args = args[2:]
scenario = os.environ.get('SMOKE_FAKE_SCENARIO', 'success')
store = Path(os.environ['SMOKE_FAKE_STATE'])
state = json.loads(store.read_text()) if store.exists() else {}
with store.with_suffix('.calls').open('a') as log:
    log.write(json.dumps(args) + '\n')
print('fake CLI stderr', file=sys.stderr)


def output(data, code=0):
    store.write_text(json.dumps(state))
    print(json.dumps(data))
    raise SystemExit(code)


def action(kind, values=None):
    return {'kind': kind, 'enabled': True, 'args': values or {}}


if args[:2] == ['game', 'bridge-health']:
    if scenario == 'timeout':
        time.sleep(2)
    if scenario == 'unreachable':
        output({'error': {'code': 'transport_connection_failed'}}, 4)
    mismatch = scenario == 'mismatch'
    health = {'status': 'game_build_mismatch' if mismatch else 'reachable_current',
              'compatibility': {'gameBuild': {'status': 'mismatch' if mismatch else 'match'}}}
    output({'error': {'code': 'game_build_mismatch'}, 'health': health} if mismatch else health,
           4 if mismatch else 0)
if args[:2] == ['dev', 'tutorials']:
    output({'enabled': False})
if args[:3] == ['dev', 'fixture', 'load']:
    fixture = Path(args[args.index('--path') + 1]).name.removeprefix('basic-').removesuffix('.sts2.fixture.yaml')
    state = {'fixture': fixture, 'played': False, 'bought': False, 'ended': False}
    output({'recipeReport': {'bridgeValidation': {'status': 'passed'},
                             'unsupportedFields': ['unsupported'] if scenario == 'unsupported' else []}})
if args[:2] == ['dev', 'wait-for-transitions']:
    output({'stable': True})
fixture = state['fixture']
if args == ['state', 'actions']:
    actions = []
    if fixture == 'combat':
        actions = [action('play-card', {'cardId': 'card-0', 'playerId': 'local', 'targetId': 'enemy'})]
        if scenario == 'missing_player_ids':
            del actions[0]['args']['playerId']
    elif fixture == 'shop-inventory':
        actions = [action(kind, {'shopItemId': 'slot-' + kind})
                   for kind in ('buy-card', 'buy-relic', 'buy-potion', 'remove-card')]
        if scenario == 'missing_shop_id':
            del actions[1]['args']['shopItemId']
        if scenario == 'missing_shop_verb':
            actions.pop()
    output({'actions': actions})
if args == ['state']:
    if scenario == 'bad_json':
        print('{broken')
        raise SystemExit(0)
    if scenario == 'unexpected_exit':
        output({'unexpected': True}, 7)
    data = {'blockingModal': None, 'rootScene': 'screens/main_menu'}
    if fixture == 'lobby':
        data['characterSelect'] = {'lobby': {'maxPlayers': 4}}
    elif fixture != 'main-menu':
        data['rootScene'] = 'run'
        data['run'] = {'view': {'playerId': 'local'}, 'players': [{'id': 'local', 'gold': 100}],
                       'currentRoom': {}, 'map': {'view': {'isOpen': True}}}
        if fixture == 'combat':
            if scenario == 'bad_player_view':
                data['run']['view'] = None
            if scenario in ('missing_player_ids', 'action_player_id_only'):
                data['run']['view'] = {}
                del data['run']['players'][0]['id']
            played = state['played'] and scenario != 'no_play_effect'
            data['run']['players'][0]['combat'] = {
                'energy': 2 if played else 3,
                'hand': {'cards': [{'id': 'card-' + str(i)} for i in range(1 if played else 0, 5)]}}
            data['run']['currentRoom']['combat'] = {
                'combatState': {'enemies': [{'id': 'enemy', 'currentHp': 12 if played else 20}]}}
        if fixture == 'shop-inventory':
            bought = state['bought']
            data['run']['players'][0]['gold'] = 70 if bought else 100
            if scenario == 'wrong_gold' and bought:
                data['run']['players'][0]['gold'] = 69
            data['run']['currentRoom'] = {'scene': 'rooms/merchant_room', 'shop': {
                'view': {'isOpen': True}, 'inventory': {'characterCardEntries': [
                    {'id': 'slot-buy-card', 'cost': 30, 'card': None if bought else {'modelId': 'test'}}]}}}
            state['stateCalls'] = state.get('stateCalls', 0) + 1
            shop = data['run']['currentRoom']['shop']
            if scenario == 'delayed_inventory' and state['stateCalls'] <= 2:
                shop['inventory'] = None
            if scenario == 'null_inventory':
                shop['inventory'] = None
            if scenario == 'missing_inventory':
                del shop['inventory']
            if scenario == 'null_shop':
                data['run']['currentRoom']['shop'] = None
            if scenario == 'missing_shop':
                del data['run']['currentRoom']['shop']
            if scenario == 'non_list_entries':
                shop['inventory'] = {'characterCardEntries': None, 'colorlessCardEntries': {}}
    output(data)
if args[0] == 'act':
    kind = args[1]
    if fixture == 'combat' and kind != 'buy-card':
        if kind == 'play-card':
            assert args[2:] == ['--card', 'card-0', '--target', 'enemy', '--player-id', 'local']
            state['played'] = True
        if kind == 'end-turn':
            state['ended'] = True
        if kind == 'cancel-end-turn':
            assert state['ended']
            if scenario == 'cancel_refused':
                output({'error': {'actionFailure': {'reasonCode': 'invalid_action'}}}, 3)
        output({'accepted': True})
    if fixture == 'shop-inventory' and kind == 'buy-card':
        assert args[2:] == ['--shop-item', 'slot-buy-card']
        state['bought'] = True
        output({'accepted': True})
    reason = 'wrong_screen' if kind in ('play-card', 'buy-card') else 'invalid_action'
    if scenario == 'wrong_refusal':
        reason = 'missing_hook'
    output({'error': {'actionFailure': {'reasonCode': reason}}}, 3)
raise AssertionError('Unexpected command: ' + repr(args))
