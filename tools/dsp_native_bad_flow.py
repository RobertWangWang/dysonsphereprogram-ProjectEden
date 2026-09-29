"""验证 20 个 bad-instruction 函数的边界证据；依赖 Capstone，仅解码，不执行。"""

def check(ok, message):
    if not ok:
        raise ValueError(message)

def main():
    import json,pathlib,struct,hashlib
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32,CS_MODE_64
    from dsp_knowledge import GENERATED
    base=GENERATED / 'native'
    inv=json.loads((base/'inventory.json').read_text()); audit=json.loads((base/'quality-audit.json').read_text()); result=[]
    for module in inv['files']:
     expected={f['address'] for f in audit['functions'] if f['module']==module['path'] and 'bad-instruction' in f['categories']}
     if not expected:continue
     path=base/module['output']/'bad-flow-audit.json'; report=json.loads(path.read_text()); data=(pathlib.Path(inv['game_directory'])/module['path']).read_bytes()
     check(hashlib.sha256(data).hexdigest()==module['sha256']==report['source_sha256'], 'Source hash mismatch')
     check({f['address'] for f in report['functions']}==expected, 'Requested function set mismatch')
     pe=struct.unpack_from('<I',data,60)[0];o=pe+24;magic=struct.unpack_from('<H',data,o)[0];bits=64 if magic==0x20b else 32
     imagebase=struct.unpack_from('<Q' if bits==64 else '<I',data,o+(24 if bits==64 else 28))[0]
     headers=struct.unpack_from('<I',data,o+60)[0];ns=struct.unpack_from('<H',data,pe+6)[0];optsize=struct.unpack_from('<H',data,pe+20)[0]
     def raw(va,size):
      rva=va-imagebase
      if 0<=rva and rva+size<=headers:return data[rva:rva+size]
      for j in range(ns):
       h=o+optsize+j*40;_,r,sz,off=struct.unpack_from('<IIII',data,h+8)
       if r<=rva and rva+size<=r+sz:return data[off+rva-r:off+rva-r+size]
      raise ValueError(hex(va))
     for f in report['functions']:
      findings=[]
      for e in f['boundary_edges']:
       if 'bytes' not in e:continue
       a=int(e['to'],16);b=bytes.fromhex(e['bytes']);check(raw(a,len(b))==b, 'Boundary byte mismatch')
       if e['decoded']:continue
       ins=list(Cs(CS_ARCH_X86,CS_MODE_64 if bits==64 else CS_MODE_32).disasm(b,a,count=1))
       first=(ins[0].mnemonic+' '+ins[0].op_str) if ins else 'unrecognized'
       kind='unresolved-boundary'
       if 0<=a-imagebase<headers:kind='PE-header-target'
       elif ins and ins[0].mnemonic in ('vprotq','vprotd'):kind='XOP-decode-gap'
       elif ins and ins[0].mnemonic=='vfmaddps':kind='FMA4-decode-gap'
       findings.append(dict(address=e['to'],from_address=e['from'],bytes=e['bytes'],decoded=first,kind=kind))
      result.append(dict(module=module['path'],address=f['address'],findings=findings,scope='boundary evidence only; transitive callees not traversed'))
    print('Verified source hashes, requested functions and raw boundary bytes for',len(result),'functions')
    (base/'bad-flow-verified.json').write_text(json.dumps(dict(functions=result,inputs={m['output']+'/bad-flow-audit.json':hashlib.sha256((base/m['output']/'bad-flow-audit.json').read_bytes()).hexdigest() for m in inv['files'] if (base/m['output']/'bad-flow-audit.json').exists()}),indent=2))
    for r in result:print(r['module'],r['address'],sorted({f['kind'] for f in r['findings']}))

if __name__ == "__main__":
    main()
