#!/usr/bin/env node
// The operator-key header is the only thing that makes a LicensedCommercial deployment usable
// from the terminal. These cases pin the two properties that matter: a write carries the key, a
// read never does, and a blank configuration changes nothing at all. Run with `npm test`.
import assert from 'node:assert/strict';
import test from 'node:test';
import { OPERATOR_KEY_HEADER, operatorKeyHeaders } from '../operatorKey.ts';

test('a write carries the configured key', () => {
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    assert.deepEqual(operatorKeyHeaders(method, 's3cret-key'), { [OPERATOR_KEY_HEADER]: 's3cret-key' });
  }
});

test('a read never carries the key, even when one is configured', () => {
  for (const method of ['GET', 'HEAD', 'OPTIONS']) {
    assert.deepEqual(operatorKeyHeaders(method, 's3cret-key'), {});
  }
});

test('lowercase and padded method names are read as the method they are', () => {
  assert.deepEqual(operatorKeyHeaders('post', 'k'), { [OPERATOR_KEY_HEADER]: 'k' });
  assert.deepEqual(operatorKeyHeaders(' get ', 'k'), {});
});

test('an unset or blank key adds no header at all', () => {
  for (const key of [undefined, '', '   ', '\t\n']) {
    assert.deepEqual(operatorKeyHeaders('POST', key), {});
  }
});

test('the key is trimmed, so a trailing newline from a dotenv file cannot break the comparison', () => {
  assert.deepEqual(operatorKeyHeaders('POST', '  s3cret-key\n'), { [OPERATOR_KEY_HEADER]: 's3cret-key' });
});

test('the header name matches the one the API guard reads', () => {
  // ComplianceOptions.OperatorKeyHeaderName — a rename on either side is a 401 for every write.
  assert.equal(OPERATOR_KEY_HEADER, 'X-PseDesk-Key');
});
