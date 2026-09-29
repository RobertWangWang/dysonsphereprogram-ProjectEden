// Experimental FMA4 control-flow C with opaque fused operation, never a semantic completion claim.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspFma4Candidate extends GhidraScript {
    @Override public void run() throws Exception {
        if (!currentProgram.getExecutableSHA256().equalsIgnoreCase("b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4")) throw new IllegalStateException("Wrong Unity baseline");
        Path base=Paths.get(getScriptArgs()[0]);
        long[][] defs={{0x1813b1070L,0x1813b53dcL},{0x1813b5460L,0x1813b96f2L},{0x1813b9780L,0x1813bd53cL}};
        for (long[] def:defs) {
            String key=Long.toHexString(def[0]);Path root=base.resolve(key),out=root.resolve("ghidra-candidate");Files.createDirectories(out);
            Address entry=toAddr(def[0]),end=toAddr(def[1]-1);AddressSet body=new AddressSet(entry,end);
            Map<String,Object> report=new LinkedHashMap<>();report.put("address",key);report.put("source_sha256",currentProgram.getExecutableSHA256());
            List<Map<String,Object>> fmas=new ArrayList<>();int count=0;
            int tx=currentProgram.startTransaction("Temporary FMA4 decode candidate");
            try {
                Function f=getFunctionAt(entry);
                if(f==null) throw new IllegalStateException("Missing original function");
                currentProgram.getListing().clearCodeUnits(entry,end,false);
                for(String line:Files.readAllLines(root.resolve("game.asm"),StandardCharsets.UTF_8)) {
                    String[] parts=line.trim().split("\\s+",3);Address a=toAddr(parts[0]);int length=parts[1].length()/2;
                    Instruction i=getInstructionAt(a);
                    if(i==null) {
                        new DisassembleCommand(a,body,true).applyTo(currentProgram,monitor);i=getInstructionAt(a);
                    }
                    if(i==null || i.getLength()!=length) throw new IllegalStateException("Instruction boundary mismatch at "+a);
                    if(!java.util.HexFormat.of().formatHex(i.getBytes()).equals(parts[1])) throw new IllegalStateException("Instruction bytes mismatch at "+a);
                    count++;
                    if(parts[2].startsWith("vfmaddps ")) {
                        if(!i.getMnemonicString().equals("VFMADDPS") || i.getNumOperands()!=4) throw new IllegalStateException("FMA decode mismatch "+a);
                        Map<String,Object> row=new LinkedHashMap<>();row.put("address",a.toString());row.put("instruction",i.toString());row.put("bytes",parts[1]);
                        List<String> operands=new ArrayList<>(),pcode=new ArrayList<>();int opaque=0;
                        for(int n=0;n<4;n++) operands.add(i.getDefaultOperandRepresentation(n));
                        for(PcodeOp op:i.getPcode()) {
                            pcode.add(op.toString());
                            if(op.getOpcode()==PcodeOp.CALLOTHER && currentProgram.getLanguage().getUserDefinedOpName((int)op.getInput(0).getOffset()).equals("DSP_FMA4_PS_RESULT_AND_MXCSR")) {
                                if(op.getNumInputs()!=5 || op.getOutput().getSize()!=20) throw new IllegalStateException("Wrong intrinsic signature");opaque++;
                            }
                        }
                        if(opaque!=1) throw new IllegalStateException("Opaque FMA operation absent/duplicated");
                        row.put("operands",operands);row.put("pcode",pcode);fmas.add(row);
                    }
                }
                if(fmas.size()!=40) throw new IllegalStateException("Expected 40 FMA4 operations");
                f.setBody(body);
                DecompInterface decompiler=new DecompInterface();decompiler.toggleSyntaxTree(false);
                if(!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
                try {
                    DecompileResults result=decompiler.decompileFunction(f,120,monitor);
                    report.put("status",result.getDecompiledFunction()!=null ? "candidate-decompiled" : "failed");report.put("message",result.getErrorMessage());
                    if(result.getDecompiledFunction()!=null) {
                        String code=result.getDecompiledFunction().getC();Files.writeString(out.resolve(key+".c"),code,StandardCharsets.UTF_8);
                        report.put("file",key+".c");report.put("contains_bad_instruction",code.toLowerCase().contains("bad instruction") || code.contains("halt_baddata"));
                        report.put("opaque_intrinsic_occurrences",code.split("DSP_FMA4_PS_RESULT_AND_MXCSR",-1).length-1);
                        report.put("warnings",code.lines().filter(s->s.contains("WARNING")).toList());
                    }
                } finally {decompiler.dispose();}
            } finally {currentProgram.endTransaction(tx,false);}
            report.put("instruction_count",count);report.put("fma4",fmas);report.put("program_changes_rolled_back",true);
            report.put("limitation","Opaque FMA4 vector/MXCSR operation; floating-point and exception semantics unmodeled. Candidate only.");
            Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report),StandardCharsets.UTF_8);
            println("DSP_FMA4_CANDIDATE "+key+" "+report.get("status")+" bad_instruction="+report.get("contains_bad_instruction"));
        }
    }
}
