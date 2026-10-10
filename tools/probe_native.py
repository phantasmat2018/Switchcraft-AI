"""Local, read-only black-box probe of the supplied TypeSwitcher C ABI."""
import ctypes as c
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
runtime = ROOT / 'TypeSwitcher'
cookie = os.add_dll_directory(str(runtime))
dll = c.CDLL(str(runtime / 'TypeSwitcherCABI.dll'))
dll.ts_engine_create.argtypes = [c.c_char_p, c.c_char_p]
dll.ts_engine_create.restype = c.c_void_p
dll.ts_engine_evaluate.argtypes = [c.c_void_p, c.c_char_p, c.c_char_p, c.POINTER(c.c_int), c.POINTER(c.c_double), c.c_void_p, c.c_int, c.c_void_p, c.c_int]
dll.ts_engine_evaluate.restype = c.c_void_p
dll.ts_free_string.argtypes = [c.c_void_p]
dll.ts_engine_destroy.argtypes = [c.c_void_p]

rules = (runtime / 'typeswitcher_rules.json').read_bytes()
engine = dll.ts_engine_create(str(runtime / 'typeswitcher_rules.json').encode(), str(runtime / 'user_preferences.json').encode())
print('Created engine:', bool(engine), flush=True)
if not engine:
    raise SystemExit(1)
results = []
try:
    for case in json.loads(rules)['golden_tests']:
        flag, confidence = c.c_int(), c.c_double()
        target, reason = c.create_string_buffer(64), c.create_string_buffer(2048)
        p = dll.ts_engine_evaluate(engine, case['input'].encode(), case.get('active_input_source', '').encode(), c.byref(flag), c.byref(confidence), target, 64, reason, 2048)
        result = c.string_at(p).decode() if p else ''
        if p:
            dll.ts_free_string(p)
        actual = result if flag.value else case['input']
        results.append({**case, 'actual': actual, 'correct': bool(flag.value), 'confidence': confidence.value, 'target': target.value.decode(), 'diagnostic': reason.value.decode(), 'matches_fixture': actual == case['expected']})
finally:
    dll.ts_engine_destroy(engine)
(ROOT / 'research/native-results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Fixture matches: {sum(x["matches_fixture"] for x in results)}/{len(results)}')
for x in results:
    print(x['input'], '=>', x['actual'], x['target'], x['diagnostic'])
