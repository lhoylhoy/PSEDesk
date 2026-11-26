#!/usr/bin/env node
// Keyboard shortcuts: the help sheet, the header hints and the handler all read one list, so a
// key can never be documented but unbound (or bound but invisible). Run with `npm test`.
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  SHORTCUT_GROUPS,
  isTypingTarget,
  keysForAction,
  resolveShortcut,
  type ShortcutActionId,
} from '../shortcuts.ts';

const ALL_ACTIONS: ShortcutActionId[] = [
  'horizon-day',
  'horizon-week',
  'horizon-month',
  'horizon-year',
  'run',
  'research',
  'trades',
  'scorecard',
  'view-simple',
  'view-pro',
  'toggle-theme',
  'guide',
  'help',
];

test('every documented action is bound to exactly one key', () => {
  for (const action of ALL_ACTIONS) {
    const keys = keysForAction(action);
    assert.equal(keys.length, 1, `${action} must be documented with one key`);
  }

  // …and every documented key resolves back to its own action.
  for (const group of SHORTCUT_GROUPS) {
    for (const item of group.items) {
      if (!item.action) {
        continue;
      }

      assert.equal(resolveShortcut({ key: item.keys[0] }), item.action, item.label);
    }
  }
});

test('the four horizons answer to 1–4 on any keyboard casing', () => {
  assert.equal(resolveShortcut({ key: '1' }), 'horizon-day');
  assert.equal(resolveShortcut({ key: '2' }), 'horizon-week');
  assert.equal(resolveShortcut({ key: '3' }), 'horizon-month');
  assert.equal(resolveShortcut({ key: '4' }), 'horizon-year');
});

test('letters are case-insensitive because Shift is not required', () => {
  assert.equal(resolveShortcut({ key: 'r' }), 'run');
  assert.equal(resolveShortcut({ key: 'R' }), 'run');
  assert.equal(resolveShortcut({ key: 'K', shiftKey: true }), 'research');
});

test('a modifier or a focused text field never loses its keystroke', () => {
  assert.equal(resolveShortcut({ key: 'r', metaKey: true }), null);
  assert.equal(resolveShortcut({ key: 'r', ctrlKey: true }), null);
  assert.equal(resolveShortcut({ key: 'p', altKey: true }), null);
  assert.equal(resolveShortcut({ key: 'r' }, { typing: true }), null);
  assert.equal(resolveShortcut({ key: '1' }, { typing: true }), null);
});

test('a non-printing key is left to the browser and the dialog primitive', () => {
  assert.equal(resolveShortcut({ key: 'Escape' }), null);
  assert.equal(resolveShortcut({ key: 'Tab' }), null);
  assert.equal(resolveShortcut({ key: 'ArrowUp' }), null);
  assert.equal(resolveShortcut({ key: 'F5' }), null);
  assert.equal(resolveShortcut({ key: 'Dead' }), null);
});

test('while a sheet is open only the shortcut list still answers', () => {
  assert.equal(resolveShortcut({ key: '?' }, { dialogOpen: true }), 'help');
  assert.equal(resolveShortcut({ key: 'r' }, { dialogOpen: true }), null);
  assert.equal(resolveShortcut({ key: '1' }, { dialogOpen: true }), null);
  // Typing still wins over the sheet rule.
  assert.equal(resolveShortcut({ key: '?' }, { dialogOpen: true, typing: true }), null);
});

test('the help sheet documents Escape without wiring it', () => {
  const help = SHORTCUT_GROUPS.find((group) => group.title === 'Help');
  assert.ok(help);

  const escape = help.items.find((item) => item.keys.includes('Esc'));
  assert.ok(escape, 'a reader looks for Escape in the help sheet');
  assert.equal(escape.action, undefined, 'Radix owns Escape; wiring it here would double-close');
});

test('typing targets are recognised structurally, without a DOM', () => {
  assert.equal(isTypingTarget(null), false);
  assert.equal(isTypingTarget(undefined), false);
  assert.equal(isTypingTarget({ tagName: 'DIV' }), false);
  assert.equal(isTypingTarget({ tagName: 'div' }), false);
  assert.equal(isTypingTarget({ tagName: 'INPUT' }), true);
  assert.equal(isTypingTarget({ tagName: 'textarea' }), true);
  assert.equal(isTypingTarget({ tagName: 'SELECT' }), true);
  assert.equal(isTypingTarget({ tagName: 'DIV', isContentEditable: true }), true);
});

test('an open picker owns the keyboard, a closed one does not', () => {
  // The trading-day Select: its portal listbox takes focus, and letters are typeahead there.
  assert.equal(isTypingTarget({ role: 'listbox' }), true);
  assert.equal(isTypingTarget({ role: 'option' }), true);
  assert.equal(isTypingTarget({ role: 'menuitem' }), true);
  assert.equal(isTypingTarget({ role: 'combobox', ariaExpanded: 'true' }), true);
  // Focus left on the closed trigger is just a button — shortcuts must keep working.
  assert.equal(isTypingTarget({ role: 'combobox', ariaExpanded: 'false' }), false);
  assert.equal(isTypingTarget({ role: 'combobox', ariaExpanded: null }), false);
  // Our own controls stay live.
  assert.equal(isTypingTarget({ role: 'tab' }), false);
  assert.equal(isTypingTarget({ tagName: 'BUTTON' }), false);
});
