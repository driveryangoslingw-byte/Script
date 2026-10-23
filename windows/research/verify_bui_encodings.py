"""Offline interpretation of a small, guarded Thumb subset from public BUI firmware.

No USB, native packages, peripheral accesses, security operations, or firmware writes.
Unknown instructions fail closed. Only known read handlers/getters execute; memcpy
and diagnostic-completion routines are observed at their original call addresses.
"""
from pathlib import Path
import argparse, hashlib, json, struct

ROOT = Path(__file__).parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('firmware', nargs='?', type=Path, default=ROOT.parent/'stm32.bin')
parser.add_argument('--output', type=Path, default=ROOT/'bui-encoding-evidence.json')
arguments = parser.parse_args()
FLASH = arguments.firmware.read_bytes()
BASE, RAM, CTX, BUF, STOP = 0x08000000, 0x20000000, 0x20008000, 0x20008100, 0x08100000
EXPECTED = 'fbcb231486d98d8d05e33f67fc07003f9d0366a543c7dc99728e82f42f3ad0a6'
assert hashlib.sha256(FLASH).hexdigest() == EXPECTED

def bl_target(pc, first, second):
    sign = (first >> 10) & 1
    i1 = 1 ^ (((second >> 13) & 1) ^ sign)
    i2 = 1 ^ (((second >> 11) & 1) ^ sign)
    delta = (sign << 24) | (i1 << 23) | (i2 << 22) | ((first & 1023) << 12) | ((second & 2047) << 1)
    if sign: delta -= 1 << 25
    return pc + 4 + delta

def literal(pc):
    instruction = struct.unpack_from('<H', FLASH, pc - BASE)[0]
    assert instruction & 0xf800 == 0x4800
    address = ((pc + 4) & ~3) + (instruction & 255) * 4
    return struct.unpack_from('<I', FLASH, address - BASE)[0]

class ReadMachine:
    def __init__(self):
        self.memory = bytearray(0x20000)
        self.registers = [0] * 16
        self.registers[0], self.registers[13], self.registers[14] = CTX, RAM + 0x10000, STOP | 1
        self.write(CTX + 8, BUF, 4)
        self.zero, self.carry, self.negative, self.overflow = False, False, False, False
        self.trace, self.nrc, self.completed = [], None, False

    def read(self, address, count):
        if BASE <= address and address + count <= BASE + len(FLASH):
            return int.from_bytes(FLASH[address-BASE:address-BASE+count], 'little')
        if RAM <= address and address + count <= RAM + len(self.memory):
            return int.from_bytes(self.memory[address-RAM:address-RAM+count], 'little')
        raise ValueError(f'Unmapped read {address:08x}')

    def write(self, address, value, count):
        assert RAM <= address and address + count <= RAM + len(self.memory), 'Only emulator RAM is writable'
        self.memory[address-RAM:address-RAM+count] = (value & ((1 << (count*8))-1)).to_bytes(count, 'little')

    def compare(self, left, right):
        result = (left - right) & 0xffffffff
        self.zero, self.carry, self.negative = result == 0, left >= right, bool(result & 0x80000000)
        self.overflow = bool(((left ^ right) & (left ^ result)) & 0x80000000)

    def run(self, entry):
        r, pc = self.registers, entry
        for _ in range(300):
            if pc == STOP: return
            h = self.read(pc, 2)
            wide = h >> 11 in (29, 30, 31)
            second = self.read(pc + 2, 2) if wide else 0
            next_pc = pc + (4 if wide else 2)
            description = ''
            if wide and h & 0xf800 == 0xf000 and second & 0xd000 == 0xd000:
                target = bl_target(pc, h, second)
                description = f'BL {target:08x}'
                if target == 0x0801ee4a:
                    assert 0 <= r[2] <= 8
                    for i in range(r[2]): self.write(r[0]+i, self.read(r[1]+i, 1), 1)
                elif target == 0x0802cce0: self.completed = True
                elif target == 0x0802cacc: self.nrc = r[0]
                else:
                    assert target in (0x0801e76a, 0x0800ca98, 0x0800ca0a), 'Only known read getters may execute'
                    r[14], next_pc = next_pc | 1, target
            elif wide and h & 0xfff0 == 0xf3c0:  # UBFX
                source, destination = h & 15, (second >> 8) & 15
                shift = (((second >> 12) & 7) << 2) | ((second >> 6) & 3)
                width = (second & 31) + 1
                r[destination] = (r[source] >> shift) & ((1 << width)-1)
                description = f'UBFX r{destination},r{source},#{shift},#{width}'
            elif wide and h == 0xf000 and second == 0x007f:
                r[0] &= 127; description = 'AND r0,r0,#127'
            elif wide and h & 0xfff0 == 0xea40:  # ORR shifted register
                source, destination, other = h & 15, (second >> 8) & 15, second & 15
                shift = (((second >> 12) & 7) << 2) | ((second >> 6) & 3)
                kind = (second >> 4) & 3
                assert kind in (0, 1)
                operand = ((r[other] << shift) & 0xffffffff) if kind == 0 else r[other] >> shift
                r[destination] = r[source] | operand
                description = f'ORR r{destination},r{source},r{other},shift={kind}:{shift}'
            elif wide: raise ValueError(f'Unsupported wide instruction {pc:08x}: {h:04x} {second:04x}')
            elif h & 0xfe00 in (0xb400, 0xbc00):
                popping = bool(h & 0x0800)
                regs = [i for i in range(8) if h & (1 << i)]
                if h & 0x100: regs.append(15 if popping else 14)
                if popping:
                    for i, reg in enumerate(regs):
                        value = self.read(r[13]+i*4, 4)
                        if reg == 15: next_pc = value & ~1
                        else: r[reg] = value
                    r[13] += len(regs)*4
                else:
                    r[13] -= len(regs)*4
                    for i, reg in enumerate(regs): self.write(r[13]+i*4, r[reg], 4)
                description = ('POP ' if popping else 'PUSH ') + str(regs)
            elif h & 0xfc00 == 0x4400:
                operation, destination, source = (h >> 8) & 3, (h & 7) | ((h >> 4) & 8), (h >> 3) & 15
                if operation == 2: r[destination] = r[source]
                elif operation == 3: next_pc = r[source] & ~1
                else: raise ValueError('Unsupported high register operation')
                description = f'HighOp {operation} r{destination},r{source}'
            elif h & 0xf800 == 0x4800:
                destination = (h >> 8) & 7; r[destination] = literal(pc)
                description = f'LDR_LITERAL r{destination}={r[destination]:08x}'
            elif h & 0xe000 == 0x2000:
                operation, register, immediate = (h >> 11) & 3, (h >> 8) & 7, h & 255
                if operation == 0: r[register] = immediate
                elif operation == 1: self.compare(r[register], immediate)
                else: raise ValueError('Unexpected arithmetic immediate')
                description = f'ImmOp {operation} r{register},#{immediate}'
            elif h & 0xe000 == 0x6000:
                count, loading = (1 if h & 0x1000 else 4), bool(h & 0x0800)
                register, base, offset = h & 7, (h >> 3) & 7, (h >> 6) & 31
                address = r[base] + offset*count
                if loading: r[register] = self.read(address, count)
                else: self.write(address, r[register], count)
                description = f'Memory {loading}:{count} r{register},[{address:08x}]'
            elif h & 0xf000 == 0x8000:
                loading, register, base = bool(h & 0x0800), h & 7, (h >> 3) & 7
                address = r[base] + ((h >> 6) & 31)*2
                if loading: r[register] = self.read(address, 2)
                else: self.write(address, r[register], 2)
                description = f'MemoryHalf {loading} r{register},[{address:08x}]'
            elif h & 0xf000 == 0x9000:
                register, address = (h >> 8) & 7, r[13]+(h & 255)*4
                if h & 0x0800: r[register] = self.read(address, 4)
                else: self.write(address, r[register], 4)
                description = f'SP memory r{register}'
            elif h & 0xf800 in (0x0000, 0x0800):
                destination, source, shift = h & 7, (h >> 3) & 7, (h >> 6) & 31
                if h & 0x0800: r[destination] = r[source] >> (shift or 32)
                else: r[destination] = (r[source] << shift) & 0xffffffff
                description = f'Shift r{destination},r{source},#{shift}'
            elif h & 0xf000 == 0xd000 and (h >> 8) & 15 < 14:
                condition = (h >> 8) & 15
                conditions = {0:self.zero, 1:not self.zero, 2:self.carry, 3:not self.carry}
                assert condition in conditions
                if conditions[condition]:
                    delta = h & 255; delta = delta-256 if delta & 128 else delta
                    next_pc = pc+4+delta*2
                description = f'ConditionalBranch {condition}'
            elif h & 0xf800 == 0xe000:
                delta = h & 2047; delta = delta-2048 if delta & 1024 else delta
                next_pc = pc+4+delta*2; description = 'Branch'
            else: raise ValueError(f'Unsupported instruction {pc:08x}: {h:04x}')
            self.trace.append({'address':f'{pc:08x}', 'bytes':FLASH[pc-BASE:next_pc-BASE].hex() if next_pc == pc+(4 if wide else 2) else f'{h:04x}', 'operation':description})
            pc = next_pc
        raise ValueError('Instruction budget exceeded')

def read_case(name, entry, address, value, count):
    machine = ReadMachine(); machine.write(address, value, count); machine.run(entry)
    size = machine.read(CTX+12, 2)
    output = machine.memory[BUF-RAM:BUF-RAM+size]
    return {'case':name, 'seed_value':value, 'output':output.hex(' '), 'nrc':machine.nrc,
            'completed':machine.completed, 'trace':machine.trace}

date_ram = literal(0x0801e76a)
language_ram = literal(0x0800ca98)
units_ram = literal(0x0800ca0a)+1
cases = [read_case('date 13 16',0x0802e872,date_ram,0x1316,4),
         read_case('language raw 01',0x0802eb34,language_ram,1,1),
         read_case('units internal 1',0x0802eb54,units_ram,1,1),
         read_case('units internal 2',0x0802eb54,units_ram,2,1),
         read_case('units invalid internal 3',0x0802eb54,units_ram,3,1)]
assert cases[0]['output'] == '13 16'
assert cases[1]['output'] == '01'
assert cases[2]['output'] == '00' and cases[3]['output'] == '01'
assert cases[4]['nrc'] == 0x22

# Statically inspect, never execute, the original date setter's UBFX checks.
# These decode the same packed halfword returned by the read getter. The
# original branch conditions reject zero day, month and year-code, then check
# day < 32 and month < 13. No calendar-year origin appears here.
date_fields = []
for offset in range(0x2e20c, 0x2e23c, 2):
    first, second = struct.unpack_from('<HH', FLASH, offset)
    if first == 0xf3c0:
        lsb = (((second >> 12) & 7) << 2) | ((second >> 6) & 3)
        width = (second & 31) + 1
        date_fields.append({'address':f'{BASE+offset:08x}', 'lsb':lsb, 'width':width,
                            'value':(0x1316 >> lsb) & ((1 << width)-1)})
assert [(field['lsb'],field['width']) for field in date_fields] == [(11,5),(7,4),(11,5),(7,4)]
assert FLASH[0x2e21e:0x2e228].hex() == '009810f07f0f13d00098'

# Resource selector 0801f1bc takes language in R1/R6 and addresses
# resource_base + (11 * text_id + language) * 4. The language-setting
# text row at index 12 makes each implemented language explicit.
assert FLASH[0x1f1bc:0x1f1c2].hex() == '70b505460e46'
assert FLASH[0x1f1e6:0x1f1f8].hex() == '05eb450000ebc500fe4901eb800050f82640'
resource_base = literal(0x0801f1ee)
language_labels = []
for code in range(11):
    pointer = struct.unpack_from('<I',FLASH,resource_base-BASE+(12*11+code)*4)[0]
    label = FLASH[pointer-BASE:FLASH.index(b'\0',pointer-BASE)].decode('utf-8') if pointer else None
    language_labels.append({'code':code,'label':label})
assert 'English' in language_labels[1]['label']

def calls_to(target):
    result = []
    for offset in range(0, len(FLASH)-4, 2):
        first, second = struct.unpack_from('<HH', FLASH, offset)
        if first & 0xf800 == 0xf000 and second & 0xd000 == 0xd000 and bl_target(BASE+offset,first,second) == target:
            result.append({'address':f'{BASE+offset:08x}', 'following_bytes':FLASH[offset+4:offset+24].hex(' ')})
    return result
language_calls = calls_to(0x0800ca98)
assert FLASH[0xbdd6:0xbddc].hex() == '054629462d20' # getter R0 -> R5 -> text-selector R1
assert bl_target(0x0800bddc,*struct.unpack_from('<HH',FLASH,0xbddc)) == 0x0801f1bc
result = {'sha256':EXPECTED,'scope':'Offline known read handlers; no USB or native packages.',
          'date_layout':{'day_bits':'15..11','month_bits':'10..7','year_code_bits':'6..0',
                         'input':'13 16','day':2,'month':6,'year_code':22,'calendar_year_base':'UNVERIFIED'},
          'date_field_checks':date_fields,
          'language_labels':language_labels,'language_getter_calls':language_calls,
          'units_wire_mapping':{'00':'internal enum 1','01':'internal enum 2'},
          'cases':cases}
arguments.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('PASS: original date/language/units read instructions interpreted; unknown instructions rejected.')
print('Language 01: English. Date 13 16: day=2 month=6 year-code=22; full year remains unverified.')
