import struct, sys, collections

def read_str(f):
    (n,) = struct.unpack('<Q', f.read(8))
    return f.read(n).decode('utf-8', 'replace')

def skip_val(f, t):
    if t == 9:
        et, cnt = struct.unpack('<IQ', f.read(12))
        if et == 8:
            for _ in range(cnt): read_str(f)
        elif et == 9:
            for _ in range(cnt): skip_val(f, 9)
        else:
            size = {0:1,1:1,7:1,2:2,3:2,4:4,5:4,6:4,10:8,11:8,12:8}[et]
            f.seek(cnt*size, 1)
    elif t == 8:
        read_str(f)
    else:
        size = {0:1,1:1,7:1,2:2,3:2,4:4,5:4,6:4,10:8,11:8,12:8}.get(t)
        if size is None: raise ValueError(t)
        f.seek(size, 1)

TYPE_NAMES = {0:'F32',1:'F16',24:'Q8_0',30:'Q6_K',28:'Q5_K_M',29:'Q5_K_S',26:'Q4_K_M',27:'Q4_K_S',
              25:'Q4_K_XS' if False else 'Q4_1',22:'Q4_0',14:'IQ4_XS',38:'IQ4_NL',20:'IQ3_XXS',21:'IQ3_S',
              24+0:'Q8_0',12:'Q2_K',16:'Q3_K_S',17:'Q3_K_M',18:'Q3_K_L',19:'IQ2',32:'IQ1_S',33:'IQ1_M',
              36:'TQ1_0',37:'TQ2_0',15:'Q5_0',13:'Q5_1',23:'Q4_1'}
def tname(t):
    names = {0:'F32',1:'F16',8:'Q8_0',12:'Q2_K',13:'Q5_1',14:'Q5_0' if False else 'Q4_0'}
    m = {0:'F32',1:'F16',2:'Q4_0',3:'Q4_1',6:'Q5_0',7:'Q5_1',8:'Q8_0',9:'Q8_1',10:'Q2_K',11:'Q2_K_S',
         12:'Q3_K_XXS' if False else 'Q2_K_M',13:'Q3_K_XS' if False else 'Q3_K_S',14:'Q3_K_S',15:'Q3_K_M',
         16:'Q3_K_L',17:'Q4_K_S',18:'Q4_K_M',19:'Q5_K_S',20:'Q5_K_M',21:'Q6_K',22:'IQ2_XXS',23:'IQ2_XS',
         24:'Q2_K_S',25:'Q3_K_XXS',26:'IQ1_S',27:'IQ4_NL',28:'Q3_K_S',29:'Q3_K_M',30:'Q3_K_L',31:'Q4_K_S',
         32:'Q4_K_M',33:'Q5_K_S',34:'Q5_K_M',35:'Q6_K',36:'IQ2_XXS',37:'IQ2_XS',38:'Q2_K_S' if False else 'Q3_K_XXS',
         39:'IQ1_S' if False else 'IQ3_XXS',40:'IQ1_M' if False else 'IQ4_XS',41:'IQ3_XXS',42:'IQ4_XS',43:'IQ3_S',
         44:'IQ3_M',45:'IQ2_S',46:'IQ2_M',47:'IQ4_XS',48:'IQ4_NL',49:'IQ3_S' if False else 'BV3',50:'IQ1_MN',
         51:'IQ2_BN',52:'IQ3_BN',53:'IQ4_BN' if False else 'TQ1_0',54:'TQ2_0',56:'MXFP4'}
    return m.get(t, f'type{t}')

path = sys.argv[1]
with open(path, 'rb') as f:
    magic = f.read(4)
    ver, = struct.unpack('<I', f.read(4))
    tensors, kvs = struct.unpack('<QQ', f.read(16))
    arch = ''; layers = 0; ctx = 0; moe = False; mtp = False; ftype = 0
    for _ in range(kvs):
        key = read_str(f)
        t, = struct.unpack('<I', f.read(4))
        if key == 'general.architecture' and t == 8:
            arch = read_str(f); continue
        if t == 9:
            et, cnt = struct.unpack('<IQ', f.read(12))
            if et == 8:
                for _ in range(cnt): read_str(f)
            elif et == 9:
                for _ in range(cnt): skip_val(f, 9)
            else:
                size = {0:1,1:1,7:1,2:2,3:2,4:4,5:4,6:4,10:8,11:8,12:8}[et]
                f.seek(cnt*size, 1)
            continue
        if t == 4 or t == 5:
            v = struct.unpack('<I', f.read(4))[0]
        elif t == 8:
            read_str(f); continue
        else:
            skip_val(f, t); continue
        if key.endswith('.block_count'): layers = v
        elif key.endswith('.context_length'): ctx = v
        elif (key.endswith('.expert_count') or key.endswith('.n_expert')) and v > 1: moe = True
        elif key.endswith('.nextn_predict_layers') and v > 0: mtp = True
        elif key == 'general.file_type': ftype = v
    hist = collections.Counter()
    hist_cpu_moe = collections.Counter()
    for _ in range(tensors):
        name = read_str(f)
        nd, = struct.unpack('<I', f.read(4))
        f.seek(8*nd, 1)
        tt, = struct.unpack('<I', f.read(4))
        f.seek(8, 1)
        if '.nextn.' in name: mtp = True
        if 'ffn_' in name and ('exps' in name or 'expert' in name or name.endswith('_moe')):
            hist_cpu_moe[('expert', tt)] += 1
        elif 'ffn_' in name:
            hist_cpu_moe[('shared_ffn', tt)] += 1
        elif 'attn_' in name:
            hist_cpu_moe[('attn', tt)] += 1
        else:
            hist_cpu_moe[('other', tt)] += 1
        hist[tt] += 1
print(f'{path.split(chr(92))[-1]}')
print(f'  arch={arch} layers={layers} ctx={ctx} MoE={moe} MTP={mtp} file_type={ftype}')
for (role, qt), n in sorted(hist_cpu_moe.items()):
    print(f'  {role:<10} {qt:<10} x{n}')
