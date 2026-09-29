// Restore float return ABI for three instruction-verified property readers; rollback.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.data.FloatDataType;
import ghidra.program.model.data.Undefined8DataType;
import ghidra.program.model.data.PointerDataType;
import ghidra.program.model.data.FunctionDefinitionDataType;
import ghidra.program.model.data.ParameterDefinition;
import ghidra.program.model.data.ParameterDefinitionImpl;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.SourceType;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairFloatReaders extends GhidraScript {
    @Override public void run() throws Exception {
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase("b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4"))throw new IllegalStateException("Wrong binary");
        Path root=Paths.get(getScriptArgs()[0]);Files.createDirectories(root);
        List<Map<String,Object>> rows=new ArrayList<>();int tx=currentProgram.startTransaction("Float reader ABI");
        DecompInterface dec=new DecompInterface();
        try {
            for(String key:List.of("180b4b7e0","180b4be10","180b52ee0")) {
                Function f=getFunctionAt(toAddr(key));f.setCallingConvention("__fastcall");
                f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS,true,SourceType.USER_DEFINED,
                    new ParameterImpl("unused",Undefined8DataType.dataType,currentProgram),new ParameterImpl("descriptor",PointerDataType.dataType,currentProgram));
                f.setReturnType(FloatDataType.dataType,SourceType.USER_DEFINED);
                FunctionDefinitionDataType signature=new FunctionDefinitionDataType("virtual_float_reader");
                signature.setReturnType(FloatDataType.dataType);signature.setCallingConvention("__fastcall");
                signature.setArguments(new ParameterDefinition[]{new ParameterDefinitionImpl("object",PointerDataType.dataType,null)});
                String tail=key.equals("180b4b7e0")?"180b4b880":key.equals("180b4be10")?"180b4be83":"180b52f2f";
                HighFunctionDBUtil.writeOverride(f,toAddr(tail),signature);
            }
            if(!dec.openProgram(currentProgram))throw new IllegalStateException(dec.getLastMessage());
            for(String key:List.of("180b4b7e0","180b4be10","180b52ee0")) {
                Function f=getFunctionAt(toAddr(key));DecompileResults result=dec.decompileFunction(f,60,monitor);
                if(result.getDecompiledFunction()==null)throw new IllegalStateException(result.getErrorMessage());
                String code=result.getDecompiledFunction().getC();Files.writeString(root.resolve(key+".c"),code);
                Map<String,Object> row=new LinkedHashMap<>();row.put("address",key);row.put("file",key+".c");row.put("signature",f.getSignature().toString());row.put("warnings",code.lines().filter(s->s.contains("WARNING")).toList());rows.add(row);
            }
        } finally {dec.dispose();currentProgram.endTransaction(tx,false);}
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("program_changes_rolled_back",true);report.put("functions",rows);
        Files.writeString(root.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("FLOAT_READERS_EXPORTED "+rows.size());
    }
}
