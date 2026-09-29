// Restore one byte-verified loop body; never persist project edits.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.data.PointerDataType;
import ghidra.program.model.data.ByteDataType;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairRailSkip extends GhidraScript {
    public void run() throws Exception {
        String sha="c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1";
        if(!sha.equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Wrong source");
        Path out=Paths.get(getScriptArgs()[0]);Files.createDirectories(out);
        Address start=toAddr("10630d30"),end=toAddr("10630d77");AddressSet body=new AddressSet(start,end);
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",sha);
        int tx=currentProgram.startTransaction("Recover truncated skip loop");DecompInterface d=new DecompInterface();
        try {
            clearListing(start,end);new DisassembleCommand(start,body,true).applyTo(currentProgram,monitor);
            for(long slot:new long[]{0x10630d78L,0x10630d7cL}) {
                if(getInt(toAddr(slot))!=0x10630d77)throw new IllegalStateException("Table differs");
            }
            currentProgram.getReferenceManager().addMemoryReference(toAddr("10630d70"),end,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);
            Function f=getFunctionAt(start);if(f==null)f=createFunction(start,"skip_classified_units_10630d30");
            if(f==null)throw new IllegalStateException("Cannot create function");f.setBody(body);
            f.setReturnType(new PointerDataType(ByteDataType.dataType),SourceType.USER_DEFINED);
            f.setCallingConvention("__cdecl");
            f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED,
                new ParameterImpl("classification_object",new PointerDataType(ByteDataType.dataType),currentProgram),
                new ParameterImpl("cursor",new PointerDataType(ByteDataType.dataType),currentProgram));
            report.put("return_type_override","byte*: EAX contains the stopping pointer; independently checked by offline original-instruction tests");
            report.put("parameter_override","cdecl: object at entry ESP+4, cursor at entry ESP+8; RET only pops return address");
            StringBuilder asm=new StringBuilder();int count=0;
            for(Instruction i:currentProgram.getListing().getInstructions(body,true)) {asm.append(i.getAddress()).append(" ").append(HexFormat.of().formatHex(i.getBytes())).append(" ").append(i).append("\n");count++;}
            Files.writeString(out.resolve("10630d30.asm"),asm);report.put("instruction_count",count);report.put("body_bytes",body.getNumAddresses());
            if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            DecompileResults r=d.decompileFunction(f,120,monitor);if(r.getDecompiledFunction()==null)throw new IllegalStateException(r.getErrorMessage());
            String code=r.getDecompiledFunction().getC();Files.writeString(out.resolve("10630d30.c"),code);
            report.put("warnings",code.lines().filter(s->s.contains("/* WARNING:")).toList());
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        report.put("program_changes_rolled_back",true);
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));
        println("RAIL_SKIP_EXPORTED");
    }
}
