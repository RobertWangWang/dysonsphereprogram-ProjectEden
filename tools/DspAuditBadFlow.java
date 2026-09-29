// Inspect suspicious terminal flows without changing the saved program.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspAuditBadFlow extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        List<Map<String,Object>> functions = new ArrayList<>();
        List<String> requested = new ArrayList<>();
        for (int a = 1; a < args.length; a++) requested.addAll(Arrays.asList(args[a].split(",")));
        for (String target : requested) {
            Function f = getFunctionAt(toAddr(target));
            if (f == null) throw new IllegalArgumentException("Missing function " + target);
            Map<String,Object> item = new LinkedHashMap<>();
            item.put("address", target);
            List<List<String>> ranges = new ArrayList<>();
            for (AddressRange r : f.getBody().getAddressRanges()) ranges.add(Arrays.asList(r.getMinAddress().toString(), r.getMaxAddress().toString()));
            item.put("ranges", ranges);
            List<Map<String,Object>> edges = new ArrayList<>();
            for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true)) {
                List<Address> targets = new ArrayList<>();
                if (i.getFallThrough() != null) targets.add(i.getFallThrough());
                if (!i.getFlowType().isCall()) targets.addAll(Arrays.asList(i.getFlows()));
                for (Address t : targets) {
                    Instruction next = getInstructionAt(t);
                    if (next != null && f.getBody().contains(t)) continue;
                    Map<String,Object> edge = new LinkedHashMap<>();
                    edge.put("from", i.getAddress().toString()); edge.put("instruction", i.toString());
                    edge.put("to", t.toString()); edge.put("decoded", next != null);
                    edge.put("in_body", f.getBody().contains(t));
                    byte[] bytes = new byte[32];
                    try { currentProgram.getMemory().getBytes(t, bytes); edge.put("bytes", HexFormat.of().formatHex(bytes)); }
                    catch (Exception e) { edge.put("read_error", e.toString()); }
                    edges.add(edge);
                }
            }
            item.put("boundary_edges", edges); functions.add(item);
        }
        report.put("functions", functions);
        Files.writeString(Paths.get(args[0]), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
        println("DSP_BAD_FLOW_AUDIT " + functions.size());
    }
}
