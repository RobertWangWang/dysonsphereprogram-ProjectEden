// Recover three truncated native bodies in an uncommitted transaction.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.data.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairRailContinuations extends GhidraScript {
    public void run() throws Exception {
        String sha="c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1";
        if(!sha.equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Wrong source");
        Path out=Paths.get(getScriptArgs()[0]);Files.createDirectories(out);
        long[][] profiles={{0x104c2d30L,0x104c2e60L,0x104c2d57L,0x104c2e64L,12},
            {0x1062fb70L,0x1062fc4bL,0x1062fbafL,0x1062fc4cL,4},
            {0x10632ce0L,0x10632d49L,0,0,0}};
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",sha);
        List<Map<String,Object>> rows=new ArrayList<>();
        int tx=currentProgram.startTransaction("Recover three body continuations");DecompInterface d=new DecompInterface();
        try {
            for(long[] p:profiles) {
                Address start=toAddr(p[0]),end=toAddr(p[1]);AddressSet body=new AddressSet(start,end);
                clearListing(start,end);new DisassembleCommand(start,body,true).applyTo(currentProgram,monitor);
                List<String> targets=new ArrayList<>();
                for(int n=0;n<p[4];n++) {
                    Address target=toAddr(Integer.toUnsignedLong(getInt(toAddr(p[3]+4*n))));
                    if(!body.contains(target))throw new IllegalStateException("Table target outside body");
                    new DisassembleCommand(target,body,true).applyTo(currentProgram,monitor);
                    currentProgram.getReferenceManager().addMemoryReference(toAddr(p[2]),target,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);
                    targets.add(target.toString());
                }
                // Only the numeric-parser interval includes alignment NOPs, unreachable after RET.
                if(p[0]==0x1062fb70L)new DisassembleCommand(toAddr("1062fbdb"),body,true).applyTo(currentProgram,monitor);
                Function f=getFunctionAt(start);if(f==null)f=createFunction(start,"recovered_"+start);
                if(f==null)throw new IllegalStateException("Cannot create function");f.setBody(body);f.setCallingConvention("__cdecl");
                DataType ptr=new PointerDataType(ByteDataType.dataType),ptrptr=new PointerDataType(ptr);
                List<Parameter> params=new ArrayList<>();
                if(p[0]==0x104c2d30L) {
                    f.setReturnType(IntegerDataType.dataType,SourceType.USER_DEFINED);
                    params.add(new ParameterImpl("object",ptr,currentProgram));params.add(new ParameterImpl("command",UnsignedIntegerDataType.dataType,currentProgram));
                    params.add(new ParameterImpl("value",UnsignedIntegerDataType.dataType,currentProgram));params.add(new ParameterImpl("argument",ptr,currentProgram));
                } else if(p[0]==0x1062fb70L) {
                    f.setReturnType(IntegerDataType.dataType,SourceType.USER_DEFINED);
                    params.add(new ParameterImpl("unused",ptr,currentProgram));params.add(new ParameterImpl("units",ptr,currentProgram));
                } else {
                    f.setReturnType(ptr,SourceType.USER_DEFINED);
                    params.add(new ParameterImpl("unused",ptr,currentProgram));params.add(new ParameterImpl("source_cursor",ptrptr,currentProgram));
                    params.add(new ParameterImpl("source_end",ptr,currentProgram));params.add(new ParameterImpl("destination_cursor",ptrptr,currentProgram));
                    params.add(new ParameterImpl("destination_end",ptr,currentProgram));
                }
                f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED,params.toArray(new Parameter[0]));
                Map<String,Object> row=new LinkedHashMap<>();row.put("address",start.toString());row.put("end",end.toString());row.put("body_bytes",body.getNumAddresses());
                row.put("table_targets",targets);row.put("abi_override","cdecl stack arguments inferred from original instructions; EAX result exposed without claiming original source type");
                StringBuilder asm=new StringBuilder();int count=0;
                for(Instruction i:currentProgram.getListing().getInstructions(body,true)) {asm.append(i.getAddress()).append(" ").append(HexFormat.of().formatHex(i.getBytes())).append(" ").append(i).append("\n");count++;}
                Files.writeString(out.resolve(start+".asm"),asm);row.put("instruction_count",count);
                if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
                DecompileResults r=d.decompileFunction(f,180,monitor);if(r.getDecompiledFunction()==null)throw new IllegalStateException(r.getErrorMessage());
                String code=r.getDecompiledFunction().getC();Files.writeString(out.resolve(start+".c"),code);row.put("warnings",code.lines().filter(s->s.contains("/* WARNING:")).toList());
                rows.add(row);d.closeProgram();println("RECOVERED_CONTINUATION "+start);
            }
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        report.put("functions",rows);report.put("program_changes_rolled_back",true);
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));
    }
}
