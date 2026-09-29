// Inspect unresolved relocation candidates without altering the program.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.*;

public class DspAuditRelocationFailures extends GhidraScript {
    @Override public void run() throws Exception {
        JsonObject input=JsonParser.parseString(Files.readString(Paths.get(getScriptArgs()[0]))).getAsJsonObject();
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase(input.get("source_sha256").getAsString()))throw new IllegalStateException("Source mismatch");
        List<Map<String,Object>> rows=new ArrayList<>();
        for(JsonElement element:input.getAsJsonArray("functions")) {
            JsonObject item=element.getAsJsonObject();String status=item.get("status").getAsString();
            if(status.equals("decompiled")||status.equals("overlaps-existing-function"))continue;
            monitor.checkCancelled();String key=item.get("address").getAsString();Address address=toAddr(key);
            Map<String,Object> row=new LinkedHashMap<>();rows.add(row);row.put("address",key);row.put("previous_status",status);
            Function owner=getFunctionContaining(address);if(owner!=null)row.put("owner",owner.getEntryPoint().toString());
            CodeUnit unit=currentProgram.getListing().getCodeUnitContaining(address);
            if(unit!=null) {row.put("unit_address",unit.getMinAddress().toString());row.put("unit_length",unit.getLength());row.put("unit_kind",unit instanceof Instruction?"instruction":unit instanceof Data?"data":"other");row.put("unit_text",unit.toString());}
            byte[] bytes=new byte[32];currentProgram.getMemory().getBytes(address,bytes);row.put("bytes_hex",HexFormat.of().formatHex(bytes));
            List<Map<String,Object>> refs=new ArrayList<>();
            for(Reference ref:getReferencesTo(address)) {
                Map<String,Object> r=new LinkedHashMap<>();refs.add(r);r.put("from",ref.getFromAddress().toString());r.put("type",ref.getReferenceType().toString());
                Instruction ins=getInstructionAt(ref.getFromAddress());if(ins!=null){r.put("instruction",ins.toString());r.put("bytes_hex",HexFormat.of().formatHex(ins.getBytes()));}
                Function f=getFunctionContaining(ref.getFromAddress());if(f!=null)r.put("owner",f.getEntryPoint().toString());
            }
            row.put("references",refs);
        }
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("entries",rows);report.put("complete",true);
        Files.writeString(Paths.get(getScriptArgs()[1]),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("RELOCATION_FAILURE_AUDIT "+rows.size());
    }
}
