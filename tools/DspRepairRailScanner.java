// Restore the verified instruction interval and both scanner switch tables, with rollback.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.data.UnsignedIntegerDataType;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairRailScanner extends GhidraScript {
    @Override public void run()throws Exception {
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase("c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1"))throw new IllegalStateException("Wrong source");
        Path output=Paths.get(getScriptArgs()[0]);Files.createDirectories(output);
        Address entry=toAddr("10632090"),end=toAddr("10632222");AddressSet body=new AddressSet(entry,end);
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("entry",entry.toString());
        int tx=currentProgram.startTransaction("Scanner instruction coverage repair");DecompInterface d=new DecompInterface();
        try {
            clearListing(entry,end);
            new DisassembleCommand(entry,body,true).applyTo(currentProgram,monitor);
            long[][] tables={{0x106320e5L,0x10632224L,11},{0x106321d9L,0x10632250L,5}};
            List<Map<String,Object>> records=new ArrayList<>();
            for(long[] table:tables) {
                List<String> targets=new ArrayList<>();
                for(int n=0;n<table[2];n++) {
                    Address target=toAddr(Integer.toUnsignedLong(getInt(toAddr(table[1]+n*4))));
                    if(!body.contains(target))throw new IllegalStateException("Target outside body");
                    new DisassembleCommand(target,body,true).applyTo(currentProgram,monitor);
                    currentProgram.getReferenceManager().addMemoryReference(toAddr(table[0]),target,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);targets.add(target.toString());
                }
                records.add(Map.of("branch",Long.toHexString(table[0]),"table",Long.toHexString(table[1]),"targets",targets));
            }
            Function f=getFunctionAt(entry);if(f==null)f=createFunction(entry,"scanner_10632090");if(f==null)throw new IllegalStateException("Cannot create scanner");f.setBody(body);
            if(getScriptArgs().length>1 && getScriptArgs()[1].equals("inline-classifier")) {
                Function classifier=getFunctionAt(toAddr("106392d0"));if(classifier==null)throw new IllegalStateException("Classifier missing");
                classifier.setReturnType(UnsignedIntegerDataType.dataType,SourceType.USER_DEFINED);classifier.setInline(true);report.put("inline_classifier","106392d0");
            }
            StringBuilder asm=new StringBuilder();int count=0;
            for(Instruction i:currentProgram.getListing().getInstructions(body,true)){asm.append(i.getAddress()).append(" ").append(HexFormat.of().formatHex(i.getBytes())).append(" ").append(i).append("\n");count++;}
            Files.writeString(output.resolve("10632090.asm"),asm);report.put("instruction_count",count);report.put("body_bytes",body.getNumAddresses());report.put("end",end.toString());report.put("tables",records);
            d.toggleSyntaxTree(false);if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            DecompileResults result=d.decompileFunction(f,180,monitor);report.put("message",result.getErrorMessage());report.put("status",result.getDecompiledFunction()==null?"failed":"decompiled");
            if(result.getDecompiledFunction()!=null){String code=result.getDecompiledFunction().getC();Files.writeString(output.resolve("10632090.c"),code);report.put("warnings",code.lines().filter(s->s.contains("/* WARNING:")).toList());}
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        report.put("program_changes_rolled_back",true);Files.writeString(output.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("RAIL_SCANNER "+report.get("status"));
    }
}
