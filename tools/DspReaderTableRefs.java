// Read references to the observed property-reader dispatch table groups.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspReaderTableRefs extends GhidraScript {
    @Override public void run() throws Exception {
        List<Map<String,Object>> rows=new ArrayList<>();
        List<String> addresses=getScriptArgs().length>1?Arrays.stream(getScriptArgs()).skip(1).flatMap(s->Arrays.stream(s.split(","))).toList():List.of("181a50be0","181a50c20","181a50c60","181a512d0");
        for(String key:addresses) {
            Map<String,Object> row=new LinkedHashMap<>();row.put("address",key);List<Map<String,Object>> refs=new ArrayList<>();
            Function owner=getFunctionContaining(toAddr(key));if(owner!=null)row.put("target_owner",owner.getEntryPoint().toString());
            Instruction targetInstruction=getInstructionAt(toAddr(key));if(targetInstruction!=null) {row.put("target_instruction",targetInstruction.toString());row.put("target_bytes",java.util.HexFormat.of().formatHex(targetInstruction.getBytes()));}
            for(Reference ref:getReferencesTo(toAddr(key))) {
                Map<String,Object> r=new LinkedHashMap<>();r.put("from",ref.getFromAddress().toString());r.put("type",ref.getReferenceType().toString());
                Instruction i=getInstructionAt(ref.getFromAddress());if(i!=null) {r.put("instruction",i.toString());r.put("bytes",java.util.HexFormat.of().formatHex(i.getBytes()));}
                Function f=getFunctionContaining(ref.getFromAddress());if(f!=null)r.put("function",f.getEntryPoint().toString());refs.add(r);
            }
            row.put("references",refs);rows.add(row);
        }
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("tables",rows);
        Files.writeString(Paths.get(getScriptArgs()[0]),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("READER_TABLE_REFERENCES "+rows.size());
    }
}
