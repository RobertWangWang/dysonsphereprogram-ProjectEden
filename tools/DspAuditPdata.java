// Audit real Ghidra body membership for PE runtime ranges without modifying the project.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.nio.file.*;
import java.util.*;
import com.google.gson.*;

public class DspAuditPdata extends GhidraScript {
    @Override public void run() throws Exception {
        JsonObject input=JsonParser.parseString(Files.readString(Paths.get(getScriptArgs()[0]))).getAsJsonObject();
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase(input.get("source_sha256").getAsString()))throw new IllegalStateException("Baseline mismatch");
        AddressSet allBodies=new AddressSet();
        for(Function f:currentProgram.getFunctionManager().getFunctions(true))allBodies.add(f.getBody());
        List<Map<String,Object>> missing=new ArrayList<>();int full=0,partial=0,empty=0;
        for(JsonElement element:input.getAsJsonArray("records")) {
            monitor.checkCancelled();JsonObject record=element.getAsJsonObject();
            Address begin=toAddr(record.get("begin").getAsString()),end=toAddr(record.get("end_exclusive").getAsString()).subtract(1);
            long size=end.subtract(begin)+1,covered=allBodies.intersectRange(begin,end).getNumAddresses();
            if(covered==size)full++;else if(covered==0)empty++;else partial++;
            if(!record.get("exact_entry_indexed").getAsBoolean() || covered!=size) {
                Map<String,Object> row=new LinkedHashMap<>();row.put("begin",begin.toString());row.put("end_exclusive",end.add(1).toString());row.put("bytes",size);row.put("covered_bytes",covered);
                Function owner=getFunctionContaining(begin);row.put("entry_owner",owner==null?null:owner.getEntryPoint().toString());
                List<List<String>> gaps=new ArrayList<>();List<Map<String,Object>> units=new ArrayList<>();
                long codeBytes=0,dataBytes=0,undefinedBytes=0;
                for(AddressRange range:new AddressSet(begin,end).subtract(allBodies).getAddressRanges()) {
                    gaps.add(Arrays.asList(range.getMinAddress().toString(),range.getMaxAddress().toString()));
                    Address cursor=range.getMinAddress();
                    while(cursor.compareTo(range.getMaxAddress())<=0) {
                        CodeUnit unit=currentProgram.getListing().getCodeUnitContaining(cursor);
                        Address stop=unit==null?cursor:unit.getMaxAddress();if(stop.compareTo(range.getMaxAddress())>0)stop=range.getMaxAddress();
                        long length=stop.subtract(cursor)+1;
                        String kind="undefined";
                        if(unit instanceof Instruction) {kind="instruction";codeBytes+=length;}
                        else if(unit instanceof Data && ((Data)unit).isDefined()) {kind="defined-data";dataBytes+=length;}
                        else undefinedBytes+=length;
                        if(!kind.equals("undefined")) {
                            Map<String,Object> detail=new LinkedHashMap<>();detail.put("begin",cursor.toString());detail.put("end_inclusive",stop.toString());detail.put("kind",kind);detail.put("display",unit.toString());units.add(detail);
                        }
                        cursor=stop.add(1);
                    }
                }
                row.put("uncovered_ranges_inclusive",gaps);row.put("uncovered_instruction_bytes",codeBytes);row.put("uncovered_defined_data_bytes",dataBytes);row.put("uncovered_undefined_bytes",undefinedBytes);row.put("uncovered_defined_units",units);missing.add(row);
            }
        }
        Map<String,Object> output=new LinkedHashMap<>();output.put("source_sha256",currentProgram.getExecutableSHA256());output.put("input_sha256",java.util.HexFormat.of().formatHex(java.security.MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(Paths.get(getScriptArgs()[0])))));output.put("fully_covered_records",full);output.put("partially_covered_records",partial);output.put("uncovered_records",empty);output.put("records",missing);
        Files.writeString(Paths.get(getScriptArgs()[1]),new GsonBuilder().setPrettyPrinting().create().toJson(output));
        println("PDATA_COVERAGE full="+full+" partial="+partial+" empty="+empty);
    }
}
