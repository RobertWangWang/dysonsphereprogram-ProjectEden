// Resolve references omitted from Ghidra's reference database through relocation operand sites.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.*;

public class DspRelocationOperandAudit extends GhidraScript {
    private Map<String,Object> instruction(Instruction i)throws Exception {
        Map<String,Object> result=new LinkedHashMap<>();result.put("address",i.getAddress().toString());result.put("bytes_hex",HexFormat.of().formatHex(i.getBytes()));result.put("text",i.toString());return result;
    }
    @Override public void run()throws Exception {
        Path folder=Paths.get(getScriptArgs()[0]),out=folder.resolve("relocation-operand-audit");Files.createDirectories(out);
        JsonObject discovery=JsonParser.parseString(Files.readString(folder.resolve("relocation-audit/report.json"))).getAsJsonObject();
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase(discovery.get("source_sha256").getAsString()))throw new IllegalStateException("Source differs");
        JsonObject triage=JsonParser.parseString(Files.readString(folder.resolve("relocation-failure-triage/report.json"))).getAsJsonObject();
        Set<String> targets=new HashSet<>();for(JsonElement element:triage.getAsJsonArray("entries")){JsonObject r=element.getAsJsonObject();if(r.get("category").getAsString().equals("data-without-decoded-reference"))targets.add(r.get("address").getAsString());}
        List<Map<String,Object>> rows=new ArrayList<>();
        for(JsonElement element:discovery.getAsJsonArray("table_entries")) {
            JsonObject r=element.getAsJsonObject();String target=r.get("target").getAsString();if(!targets.contains(target))continue;monitor.checkCancelled();
            String slot=r.get("slot").getAsString();Map<String,Object> row=new LinkedHashMap<>();rows.add(row);row.put("target",target);row.put("slot",slot);
            Instruction i=currentProgram.getListing().getInstructionContaining(toAddr(slot));
            if(i!=null) {
                row.put("instruction",instruction(i));Function f=getFunctionContaining(i.getAddress());if(f!=null)row.put("owner",f.getEntryPoint().toString());
                List<Map<String,Object>> before=new ArrayList<>();Instruction prev=i;
                for(int n=0;n<6;n++){prev=getInstructionBefore(prev.getAddress());if(prev==null)break;before.add(instruction(prev));}row.put("before",before);
                Instruction next=getInstructionAfter(i.getAddress());if(next!=null)row.put("next",instruction(next));
            }
        }
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("entries",rows);report.put("complete",true);
        Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("RELOCATION_OPERAND_AUDIT "+rows.size());
    }
}
