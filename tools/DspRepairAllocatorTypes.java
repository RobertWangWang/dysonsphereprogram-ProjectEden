// Restore evidence-based allocator ABI and callback signatures; roll back all edits.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairAllocatorTypes extends GhidraScript {
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
        DataType ptr=new PointerDataType(VoidDataType.dataType),str=new PointerDataType(CharDataType.dataType),u=UnsignedIntegerDataType.dataType,i=IntegerDataType.dataType;
        DataType[][] types={{u,str,i},{ptr,u,str,i},{ptr,str,i}};
        String[][] names={{"size","file","line"},{"memory","size","file","line"},{"memory","file","line"}};
        DataType[] returns={ptr,ptr,VoidDataType.dataType};long[] entries={0x104c5f90L,0x104c5fe0L,0x104c5fc0L};DataType[] callbackPointers=new DataType[3];
        int tx=currentProgram.startTransaction("Allocator ABI evidence");DecompInterface d=new DecompInterface();List<Map<String,Object>> rows=new ArrayList<>();
        try {
            for(int n=0;n<3;n++) {
                signature(entries[n],returns[n],types[n],names[n]);
                FunctionDefinitionDataType def=new FunctionDefinitionDataType(new String[]{"dsp_malloc_callback","dsp_realloc_callback","dsp_free_callback"}[n]);
                def.setReturnType(returns[n]);def.setCallingConvention("__cdecl");ParameterDefinition[] args=new ParameterDefinition[types[n].length];
                for(int k=0;k<args.length;k++)args[k]=new ParameterDefinitionImpl(names[n][k],types[n][k],null);
                def.setArguments(args);callbackPointers[n]=new PointerDataType(def);
                Address slot=toAddr(0x10e1d01cL+n*4);clearListing(slot,slot.add(3));createData(slot,callbackPointers[n]);
            }
            signature(0x1060d1b8L,ptr,new DataType[]{u},new String[]{"size"});
            signature(0x1060dc5dL,ptr,new DataType[]{ptr,u},new String[]{"memory","size"});
            signature(0x104c61d0L,VoidDataType.dataType,new DataType[]{new PointerDataType(callbackPointers[0]),new PointerDataType(callbackPointers[1]),new PointerDataType(callbackPointers[2])},new String[]{"malloc_out","realloc_out","free_out"});
            if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            for(long entry:new long[]{0x104c5f90L,0x104c5fe0L,0x104c5fc0L,0x104c61d0L}) {
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
        report.put("annotation_scope","cdecl ABI, pointer returns, file/line metadata and callback types inferred from verified dispatch. Not original debug symbols or a compiled equivalence proof.");
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("ALLOCATOR_TYPES_EXPORTED");
    }
}
