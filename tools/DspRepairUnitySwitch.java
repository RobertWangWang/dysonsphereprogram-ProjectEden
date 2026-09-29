// Repair verified bounded byte-indexed RVA tables in this exact Unity binary.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.JumpTable;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairUnitySwitch extends GhidraScript {
    @Override public void run() throws Exception {
        if (!currentProgram.getExecutableSHA256().equalsIgnoreCase("b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4")) throw new IllegalStateException("Unexpected binary");
        Path output = Paths.get(getScriptArgs()[0]); Files.createDirectories(output);
        String selected = getScriptArgs().length > 1 ? getScriptArgs()[1] : "181255510";
        Address entry = toAddr(selected), end;
        long[][] defs;
        if (selected.equals("181255510")) {
            end=toAddr("1812556ef");
            defs=new long[][] {{0x1812555d6L,0x1812556f0L,0x1812556f8L,0x1812555bfL,19},
                               {0x181255669L,0x18125570cL,0x181255714L,0x181255652L,19}};
        } else if (selected.equals("18071abf0")) {
            end=toAddr("18071aeea");
            defs=new long[][] {{0x18071aca0L,0x18071aeecL,0x18071aef4L,0x18071ac80L,25}};
        } else if (selected.equals("18071af10")) {
            end=toAddr("18071b265");
            defs=new long[][] {{0x18071b009L,0x18071b268L,0x18071b270L,0x18071aff2L,25}};
        } else if (selected.equals("180cd2ed0")) {
            end=toAddr("180cd33a1");
            defs=new long[][] {{0x180cd3062L,0x180cd33a4L,0x180cd33acL,0,47}};
        } else if (selected.equals("180e91c60")) {
            end=toAddr("180e924d7");
            defs=new long[][] {{0x180e91d72L,0x180e924d8L,0x180e924e0L,0,3}};
        } else if (selected.equals("180961d40")) {
            end=toAddr("180961ead");
            defs=new long[][] {{0x180961e53L,0x180961eb0L,0,0x180961e21L,6,7}};
        } else if (selected.equals("18177f9f0")) {
            end=toAddr("18177fb3b");
            defs=new long[][] {
                {0x18177fa36L,0x18177fb3cL,0,0x18177fa1dL,15,16},
                {0x18177fa8bL,0x18177fb7cL,0,0x18177fa1dL,15,16},
                {0x18177fac0L,0x18177fbbcL,0,0x18177fab2L,15,16},
                {0x18177fad6L,0x18177fbfcL,0x18177fc04L,0x18177fab2L,15,2},
                {0x18177fae9L,0x18177fc14L,0,0x18177fadbL,15,16},
                {0x18177faffL,0x18177fc54L,0x18177fc5cL,0x18177fadbL,15,2}};
        } else throw new IllegalArgumentException("Unverified function selector");
        Function f = getFunctionAt(entry);
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("address", entry.toString());
        List<Map<String,Object>> tables = new ArrayList<>();
        int tx = currentProgram.startTransaction("Verified bounded Unity switch tables");
        try {
            for (long[] d : defs) {
                Address branch = toAddr(d[0]), table = toAddr(d[1]), index = toAddr(d[2]);
                if ((getByte(branch)&255)!=255 || (getByte(branch.add(1))&255)!=(d[3]==0 ? 226 : 225) || (d[3]!=0 && (getByte(toAddr(d[3]))&255)!=d[4])) throw new IllegalStateException("Guard/jump mismatch");
                ArrayList<Address> targets = new ArrayList<>();
                int targetCount = d.length > 5 ? (int)d[5] : 2;
                for (int i=0;i<targetCount;i++) {
                    Address target = currentProgram.getImageBase().add(Integer.toUnsignedLong(getInt(table.add(i*4))));
                    if (target.compareTo(entry)<0 || target.compareTo(end)>0) throw new IllegalStateException("Target out of range " + target);
                    if (getInstructionAt(target)==null) {
                        DisassembleCommand command = new DisassembleCommand(target,new AddressSet(entry,end),true);
                        command.applyTo(currentProgram,monitor);
                    }
                    if (getInstructionAt(target)==null) throw new IllegalStateException("Undecoded target " + target);
                    targets.add(target);
                }
                List<String> mapping = new ArrayList<>();
                for (int i=0;i<=d[4];i++) {
                    int value = d[2] == 0 ? i : getByte(index.add(i))&255;
                    if (value>=targetCount) throw new IllegalStateException("Unexpected case index");
                    mapping.add(targets.get(value).toString());
                }
                List<String> removed = new ArrayList<>();
                for (Reference ref : currentProgram.getReferenceManager().getReferencesFrom(branch)) {
                    if (ref.getReferenceType().isFlow()) {
                        removed.add(ref.getToAddress().toString());
                        currentProgram.getReferenceManager().delete(ref);
                    }
                }
                for (Address target: targets) currentProgram.getReferenceManager().addMemoryReference(branch,target,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);
                boolean override = d[2] != 0 || selected.equals("180961d40") || (selected.equals("18177f9f0") && d[0] == 0x18177fa8bL);
                if (override) new JumpTable(branch,selected.equals("180961d40")?new ArrayList<Address>(new LinkedHashSet<Address>(targets)):targets,true,0).writeOverride(f);
                Map<String,Object> item = new LinkedHashMap<>();
                item.put("branch",branch.toString()); item.put("rva_table",table.toString()); item.put("byte_index_table",index.toString());
                item.put("input_range",d[3]!=0 ? "unsigned 0.."+d[4]+"; values outside use preceding JA default" :
                    selected.equals("180cd2ed0") ? "derived index set {44,46,47}; prefix of 48 entries verified" : "derived index set {0,3}; prefix of 4 entries verified");
                item.put("targets_by_input",mapping); item.put("removed_flow_targets",removed); tables.add(item);
                item.put("manual_override",override);
            }
            f.setBody(new AddressSet(entry,end));
            DecompInterface decompiler = new DecompInterface(); decompiler.toggleSyntaxTree(false);
            if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
            try {
                DecompileResults result = decompiler.decompileFunction(f,120,monitor);
                report.put("status",result.getDecompiledFunction()!=null ? "decompiled" : "failed");
                report.put("message",result.getErrorMessage());
                if(result.getDecompiledFunction()!=null) {
                    String code=result.getDecompiledFunction().getC();
                    Files.writeString(output.resolve(selected+".c"),code,StandardCharsets.UTF_8);
                    report.put("file",selected+".c");
                    report.put("contains_bad_instruction",code.toLowerCase().contains("bad instruction"));
                    report.put("contains_warning",code.contains("WARNING"));
                }
            } finally {decompiler.dispose();}
        } finally {currentProgram.endTransaction(tx,false);}
        report.put("tables",tables); report.put("program_changes_rolled_back",true);
        Files.writeString(output.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report),StandardCharsets.UTF_8);
        println("DSP_UNITY_SWITCH " + report.get("status") + " warning=" + report.get("contains_warning"));
    }
}
