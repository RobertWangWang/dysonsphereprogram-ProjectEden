// Discover and export statically verified CRT table callback entries, with rollback.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspCrtCallbacks extends GhidraScript {
    @Override public void run() throws Exception {
        String expectedSha="b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4";
        if(getScriptArgs().length>2)expectedSha=com.google.gson.JsonParser.parseString(Files.readString(Paths.get(getScriptArgs()[2]))).getAsJsonObject().get("source_sha256").getAsString();
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase(expectedSha))throw new IllegalStateException("Wrong source binary");
        Path input=Paths.get(getScriptArgs()[0]),output=Paths.get(getScriptArgs()[1]);Files.createDirectories(output.resolve("functions"));
        boolean exitMode=getScriptArgs().length>2;boolean tableMode=false;
        Set<Long> table=new HashSet<>();
        if(exitMode) {
            com.google.gson.JsonObject evidence=com.google.gson.JsonParser.parseString(Files.readString(Paths.get(getScriptArgs()[2]))).getAsJsonObject();
            if(evidence.has("direct_entries")) {
                tableMode=true;
                if(!evidence.get("source_sha256").getAsString().equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Branch baseline mismatch");
                for(com.google.gson.JsonElement item:evidence.getAsJsonArray("direct_entries")) {
                    com.google.gson.JsonObject record=item.getAsJsonObject();Address site=toAddr(record.get("site").getAsString());
                    long target=Long.parseUnsignedLong(record.get("target").getAsString(),16);
                    byte[] expected=java.util.HexFormat.of().parseHex(record.get("bytes_hex").getAsString()),actual=new byte[expected.length];
                    currentProgram.getMemory().getBytes(site,actual);
                    if(!Arrays.equals(actual,expected) || actual.length!=5 || ((actual[0]&255)!=0xe8 && (actual[0]&255)!=0xe9) || site.getOffset()+5+currentProgram.getMemory().getInt(site.add(1))!=target)throw new IllegalStateException("Unverified direct CALL/JMP target");
                    table.add(target);
                }
            } else if(evidence.has("table_entries")) {
                tableMode=true;
                if(!evidence.get("source_sha256").getAsString().equalsIgnoreCase(currentProgram.getExecutableSHA256()))throw new IllegalStateException("Table baseline mismatch");
                for(com.google.gson.JsonElement item:evidence.getAsJsonArray("table_entries")) {
                    com.google.gson.JsonObject record=item.getAsJsonObject();long target=Long.parseUnsignedLong(record.get("target").getAsString(),16);
                    int width=evidence.has("pointer_size")?evidence.get("pointer_size").getAsInt():8;
                    if(width!=currentProgram.getDefaultPointerSize())throw new IllegalStateException("Pointer size mismatch");
                    Address slot=toAddr(record.get("slot").getAsString());
                    long value=width==4?Integer.toUnsignedLong(currentProgram.getMemory().getInt(slot)):currentProgram.getMemory().getLong(slot);
                    if(value!=target)throw new IllegalStateException("Table slot mismatch");
                    table.add(target);
                }
            } else {
            for(com.google.gson.JsonElement item:evidence.getAsJsonArray("registrations")) {
                com.google.gson.JsonObject record=item.getAsJsonObject();Address setup=toAddr(record.get("setup").getAsString()),call=toAddr(record.get("call").getAsString());
                long target=Long.parseUnsignedLong(record.get("target").getAsString(),16);
                byte[] prefix=new byte[3];currentProgram.getMemory().getBytes(setup,prefix);
                if(!Arrays.equals(prefix,new byte[]{0x48,(byte)0x8d,0x0d}) || setup.getOffset()+7+currentProgram.getMemory().getInt(setup.add(3))!=target)throw new IllegalStateException("Bad exit argument evidence");
                int opcode=currentProgram.getMemory().getByte(call)&255;
                if((opcode!=0xe8 && opcode!=0xe9) || call.getOffset()+5+currentProgram.getMemory().getInt(call.add(1))!=0x181813ca4L)throw new IllegalStateException("Bad atexit call evidence");
                table.add(target);
            }
            }
        } else for(long a=0x1818910b0L;a<0x181896f08L;a+=8)table.add(currentProgram.getMemory().getLong(toAddr(a)));
        List<Map<String,Object>> rows=new ArrayList<>();
        int tx=currentProgram.startTransaction("Temporary CRT callback discovery");
        DecompInterface decompiler=new DecompInterface();decompiler.toggleSyntaxTree(false);
        try {
            if(!decompiler.openProgram(currentProgram))throw new IllegalStateException(decompiler.getLastMessage());
            for(String line:Files.readAllLines(input,StandardCharsets.UTF_8)) {
                if(line.isBlank())continue;monitor.checkCancelled();String key=line.trim();Address entry=toAddr(key);
                if(!table.contains(entry.getOffset()) || entry.getOffset()==0)throw new IllegalStateException("Entry not in CRT table: "+key);
                Map<String,Object> row=new LinkedHashMap<>();row.put("address",key);rows.add(row);
                try {
                    Function f=getFunctionAt(entry);row.put("already_in_project",f!=null);
                    if(f==null) {
                        Function owner=getFunctionContaining(entry);
                        if(owner!=null) {row.put("status","overlaps-existing-function");row.put("owner",owner.getEntryPoint().toString());continue;}
                        if(getInstructionAt(entry)==null && !disassemble(entry)) {row.put("status","disassembly-failed");continue;}
                        f=createFunction(entry,(tableMode?"table_callback_":exitMode?"exit_callback_":"crt_callback_")+key);
                    }
                    if(f==null) {row.put("status","function-creation-failed");continue;}
                    row.put("name",f.getName());row.put("signature",f.getSignature().toString());row.put("size",f.getBody().getNumAddresses());
                    List<List<String>> ranges=new ArrayList<>();
                    for(AddressRange range:f.getBody().getAddressRanges())ranges.add(Arrays.asList(range.getMinAddress().toString(),range.getMaxAddress().toString()));
                    row.put("body_ranges_inclusive",ranges);
                    StringBuilder assembly=new StringBuilder();int count=0;
                    for(Instruction i:currentProgram.getListing().getInstructions(f.getBody(),true)) {
                        assembly.append(i.getAddress()).append(" ").append(java.util.HexFormat.of().formatHex(i.getBytes())).append(" ").append(i.toString()).append("\n");count++;
                    }
                    Files.writeString(output.resolve("functions/"+key+".asm"),assembly,StandardCharsets.UTF_8);row.put("instruction_count",count);
                    int timeout=getScriptArgs().length>3?Integer.parseInt(getScriptArgs()[3]):30;
                    if(timeout<1 || timeout>1800)throw new IllegalArgumentException("Invalid timeout");
                    row.put("timeout_seconds",timeout);
                    decompiler.flushCache();DecompileResults result=decompiler.decompileFunction(f,timeout,monitor);
                    row.put("message",result.getErrorMessage());
                    if(result.getDecompiledFunction()!=null) {
                        String code=result.getDecompiledFunction().getC();Files.writeString(output.resolve("functions/"+key+".c"),code,StandardCharsets.UTF_8);
                        row.put("status","decompiled");row.put("file","functions/"+key+".c");
                        row.put("warnings",code.lines().filter(s->s.contains("WARNING")).toList());
                    } else row.put("status","decompile-failed");
                } catch(Exception e) {row.put("status","error");row.put("message",e.toString());}
                if(rows.size()%100==0) {save(output,rows,false);println("CRT_CALLBACK_PROGRESS "+rows.size());}
            }
        } finally {decompiler.dispose();currentProgram.endTransaction(tx,false);}
        save(output,rows,true);println("CRT_CALLBACK_FINISHED "+rows.size());
    }
    private void save(Path output,List<Map<String,Object>> rows,boolean complete)throws Exception {
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("complete",complete);
        report.put("program_changes_rolled_back",complete);report.put("functions",rows);
        Files.writeString(output.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report),StandardCharsets.UTF_8);
    }
}
