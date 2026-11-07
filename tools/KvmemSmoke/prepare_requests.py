"""Opt-in smoke input generation using the release's own tokenizer (no model download)."""
import json
import random
import sys
from pathlib import Path

bundle, model, output = map(Path, sys.argv[1:4])
sys.path.insert(0, str(bundle / 'backends' / 'polystrata' / 'tools'))
from strata_tokenizer import Tokenizer

tok = Tokenizer.from_gguf(model)
rng = random.Random(306086)
records = '\n'.join(f'Archive row {i}: region {rng.choice(["east", "west", "north", "south"])}; revision {rng.randrange(10000,99999)}; checksum {rng.getrandbits(48):012x}; routine storage inventory.' for i in range(7000))
all_tokens = tok.encode(records)
requests = [{
    'label': 'short', 'minPromptTokens': 1,
    'body': {'model': model.stem.lower(), 'messages': [{'role': 'user', 'content': 'Say OK.'}], 'max_tokens': 32, 'reasoning_effort': 'none', 'temperature': 0}
}]
for size in (32768, 65536):
    # The hidden needle lies outside the sink and the resident recent window.
    part_a = tok.decode(all_tokens[:size // 4])
    part_b = tok.decode(all_tokens[size // 4:size])
    needle = '\nCritical registry: project Blue Lantern access code is 739251.\n'
    document = part_a + needle + part_b
    question = '\nQuestion: What is the access code for project Blue Lantern? Reply only with the six digits.'
    content = 'Read the following archive and answer the final question.\n' + document + question
    body = {'model': model.stem.lower(), 'messages': [{'role': 'user', 'content': content}],
            'max_tokens': 64, 'reasoning_effort': 'none', 'temperature': 0,
            'kvmem_query': {'message': 0, 'start': len(content) - len(question), 'end': len(content)}}
    requests.append({'label': f'ctx{size}', 'minPromptTokens': size, 'expected': '739251', 'body': body})
    if size == 65536:
        follow = dict(body)
        follow['messages'] = body['messages'] + [{'role': 'assistant', 'content': '739251'},
                                               {'role': 'user', 'content': 'Repeat the access code of project Blue Lantern from the archive. Only six digits.'}]
        follow.pop('kvmem_query')
        requests.append({'label': 'ctx65536-reuse', 'minPromptTokens': size, 'expected': '739251', 'body': follow})
    print(f'ctx{size}: user content tokens={len(tok.encode(content))}, characters={len(content)}', flush=True)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(requests, ensure_ascii=False), encoding='utf-8')
print(output)
