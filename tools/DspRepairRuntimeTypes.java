// Apply verified runtime ABI annotations in a rolled-back transaction.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairRuntimeTypes extends GhidraScript {
    private void signature(long address,String convention,DataType ret,DataType[] types,String[] names)throws Exception {
        Function f=getFunctionAt(toAddr(address));if(f==null)throw new IllegalStateException("Missing function "+Long.toHexString(address));
        f.setCallingConvention(convention);f.setReturnType(ret,SourceType.USER_DEFINED);
        Parameter[] params=new Parameter[types.length];for(int i=0;i<types.length;i++)params[i]=new ParameterImpl(names[i],types[i],currentProgram);
        f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED,params);
    }
    public void run()throws Exception {
        String sha="c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1";
        if(!sha.equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Wrong source");
        Path out=Paths.get(getScriptArgs()[0]);Files.createDirectories(out);
        DataType ptr=new PointerDataType(VoidDataType.dataType),u=UnsignedIntegerDataType.dataType,i=IntegerDataType.dataType,v=VoidDataType.dataType,b=BooleanDataType.dataType;
        int tx=currentProgram.startTransaction("Runtime ABI evidence");DecompInterface d=new DecompInterface();List<Map<String,Object>> rows=new ArrayList<>();
        long[] entries={0x106132d5L,0x10613316L,0x1061332dL,0x1061335eL,0x10618a07L,0x106183eaL,0x10618486L,0x10613c79L};
        try {
            Address init=toAddr(0x106132d5L);
            if(getFunctionAt(init)==null)currentProgram.getFunctionManager().createFunction("dsp_crt_initialize_locks",init,new AddressSet(init,init.add(64)),SourceType.USER_DEFINED);
            signature(0x106132d5L,"__cdecl",b,new DataType[]{},new String[]{});
            signature(0x1061332dL,"__cdecl",b,new DataType[]{},new String[]{});
            for(long a:new long[]{0x10613316L,0x1061335eL})signature(a,"__cdecl",v,new DataType[]{i},new String[]{"lock_id"});
            signature(0x10618a07L,"__stdcall",i,new DataType[]{ptr,u,u},new String[]{"critical_section","spin_count","flags"});
            signature(0x106183eaL,"__cdecl",ptr,new DataType[]{u,new PointerDataType(CharDataType.dataType),new PointerDataType(u),new PointerDataType(u)},new String[]{"function_id","name","modules_begin","modules_end"});
            signature(0x10618486L,"__cdecl",ptr,new DataType[]{u},new String[]{"module_id"});
            signature(0x10613c79L,"__cdecl",ptr,new DataType[]{u},new String[]{"size"});
            signature(0x105d29e6L,"__fastcall",v,new DataType[]{u},new String[]{"cookie"});
            getFunctionAt(toAddr(0x105d29e6L)).setInline(true);
            // Preserve the physically present return path under explicit returning OS models.
            // This is not a claim that real self-termination returns.
            signature(0x105d2db8L,"__cdecl",i,new DataType[]{},new String[]{});
            getFunctionAt(toAddr(0x105d2db8L)).setNoReturn(false);
            Function compatibility=getFunctionAt(toAddr(0x10618a07L));
            FunctionDefinitionDataType guard=new FunctionDefinitionDataType("dsp_guard_target");
            guard.setReturnType(v);guard.setCallingConvention("__fastcall");
            guard.setArguments(new ParameterDefinition[]{new ParameterDefinitionImpl("target",ptr,null)});
            HighFunctionDBUtil.writeOverride(compatibility,toAddr(0x10618a42L),guard);
            FunctionDefinitionDataType modern=new FunctionDefinitionDataType("dsp_initialize_critical_section_ex");
            modern.setReturnType(i);modern.setCallingConvention("__stdcall");
            modern.setArguments(new ParameterDefinition[]{new ParameterDefinitionImpl("critical_section",ptr,null),new ParameterDefinitionImpl("spin_count",u,null),new ParameterDefinitionImpl("flags",u,null)});
            HighFunctionDBUtil.writeOverride(compatibility,toAddr(0x10618a48L),modern);
            if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            for(long entry:entries) {
                Function f=getFunctionAt(toAddr(entry));String address=Long.toHexString(entry);Map<String,Object> row=new LinkedHashMap<>();
                row.put("address",address);row.put("signature",f.getSignature().getPrototypeString());row.put("convention",f.getCallingConventionName());row.put("return_storage",f.getReturn().getVariableStorage().toString());
                List<List<String>> ranges=new ArrayList<>();for(AddressRange range:f.getBody())ranges.add(List.of(range.getMinAddress().toString(),range.getMaxAddress().toString()));row.put("ranges",ranges);
                StringBuilder asm=new StringBuilder();int count=0;for(Instruction ins:currentProgram.getListing().getInstructions(f.getBody(),true)) {asm.append(ins.getAddress()).append(" ").append(HexFormat.of().formatHex(ins.getBytes())).append(" ").append(ins).append("\n");count++;}
                Files.writeString(out.resolve(address+".asm"),asm);row.put("instructions",count);
                DecompileResults r=d.decompileFunction(f,120,monitor);if(r.getDecompiledFunction()==null)throw new IllegalStateException(r.getErrorMessage());String code=r.getDecompiledFunction().getC();
                Files.writeString(out.resolve(address+".c"),code);row.put("warnings",code.lines().filter(s->s.contains("/* WARNING:")).toList());rows.add(row);
            }
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",sha);report.put("functions",rows);report.put("program_changes_rolled_back",true);
        report.put("compatibility_overrides",List.of("10618a42: void fastcall guard(target in ECX)","10618a48: int stdcall target(critical_section,spin_count,flags)","105d29e6: inline original cookie check for EAX preservation"));
        report.put("security_report_override","105d2db8: int cdecl, noreturn=false; retain original RET path under explicit returning termination API models");
        report.put("annotation_scope","AL bool init/cleanup, void lock wrappers, 32-bit stdcall compatibility result, pointer cache/malloc returns and observed arguments; call-site ABI overrides and original cookie-check inlining. Original code unchanged, no compiled-C equivalence proof.");
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("RUNTIME_TYPES_EXPORTED");
    }
}
