// Restore evidence-based copy ABI and an explicit dispatch table; roll back all edits.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.pcode.JumpTable;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairCopyTypes extends GhidraScript {
    private void signature(long address,DataType ret,DataType[] types,String[] names)throws Exception {
        Function f=getFunctionAt(toAddr(address));if(f==null)throw new IllegalStateException("Missing function "+Long.toHexString(address));
        f.setCallingConvention("__cdecl");f.setReturnType(ret,SourceType.USER_DEFINED);
        Parameter[] params=new Parameter[types.length];for(int i=0;i<types.length;i++)params[i]=new ParameterImpl(names[i],types[i],currentProgram);
        f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED,params);
    }
    public void run()throws Exception {
        String sha="c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1";
        if(!sha.equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Wrong source");
        Path out=Paths.get(getScriptArgs()[0]);Files.createDirectories(out);
        DataType ptr=new PointerDataType(VoidDataType.dataType),u=UnsignedIntegerDataType.dataType;
        int tx=currentProgram.startTransaction("Copy ABI evidence");DecompInterface d=new DecompInterface();List<Map<String,Object>> rows=new ArrayList<>();
        try {
            for(long entry:new long[]{0x105f9bf0L,0x105fafc0L}) {
                signature(entry,ptr,new DataType[]{ptr,ptr,u},new String[]{"destination","source","size"});
                Function f=getFunctionAt(toAddr(entry));Address branch=toAddr(entry+0x225);
                ArrayList<Address> targets=new ArrayList<>();
                for(int n=0;n<4;n++) {
                    long value=Integer.toUnsignedLong(currentProgram.getMemory().getInt(toAddr(entry+0x264+n*4)));
                    if(value<entry||value>=entry+0x574)throw new IllegalStateException("Unexpected dispatch target");
                    Address target=toAddr(value);targets.add(target);
                    currentProgram.getReferenceManager().addMemoryReference(branch,target,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);
                }
                new JumpTable(branch,targets,true,0).writeOverride(f);
            }
            if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            for(long entry:new long[]{0x105f9bf0L,0x105fafc0L}) {
                Function f=getFunctionAt(toAddr(entry));String address=Long.toHexString(entry);Map<String,Object> row=new LinkedHashMap<>();
                row.put("address",address);row.put("signature",f.getSignature().getPrototypeString());
                List<List<String>> ranges=new ArrayList<>();for(AddressRange range:f.getBody())ranges.add(List.of(range.getMinAddress().toString(),range.getMaxAddress().toString()));row.put("ranges",ranges);
                StringBuilder asm=new StringBuilder();int count=0;for(Instruction ins:currentProgram.getListing().getInstructions(f.getBody(),true)) {asm.append(ins.getAddress()).append(" ").append(HexFormat.of().formatHex(ins.getBytes())).append(" ").append(ins).append("\n");count++;}
                Files.writeString(out.resolve(address+".asm"),asm);row.put("instructions",count);
                DecompileResults r=d.decompileFunction(f,120,monitor);if(r.getDecompiledFunction()==null)throw new IllegalStateException(r.getErrorMessage());String code=r.getDecompiledFunction().getC();
                Files.writeString(out.resolve(address+".c"),code);row.put("warnings",code.lines().filter(s->s.contains("/* WARNING:")).toList());rows.add(row);
            }
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",sha);report.put("functions",rows);report.put("program_changes_rolled_back",true);
        report.put("annotation_scope","cdecl ABI, pointer return and three arguments inferred from original copy tests; explicit four-target override at entry+0x225 using original table entry+0x264. Not debug symbols or compiled equivalence proof.");
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("COPY_TYPES_EXPORTED");
    }
}
