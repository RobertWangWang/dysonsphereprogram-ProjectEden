// Export body ranges and instruction flows for function coverage audits.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspInspectFunction extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        Function function = getFunctionAt(toAddr(args[1]));
        if (function == null) throw new IllegalArgumentException("No function");
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("name", function.getName(true));
        report.put("entry", function.getEntryPoint().toString());
        report.put("body_size", function.getBody().getNumAddresses());
        List<List<String>> ranges = new ArrayList<>();
        for (AddressRange range : function.getBody().getAddressRanges()) ranges.add(Arrays.asList(range.getMinAddress().toString(), range.getMaxAddress().toString()));
        report.put("ranges", ranges);
        List<Map<String,Object>> instructions = new ArrayList<>();
        for (Instruction instruction : currentProgram.getListing().getInstructions(function.getBody(), true)) {
            Map<String,Object> item = new LinkedHashMap<>();
            item.put("address", instruction.getAddress().toString());
            item.put("size", instruction.getLength());
            item.put("text", instruction.toString());
            item.put("flow_type", instruction.getFlowType().toString());
            item.put("computed", instruction.getFlowType().isComputed());
            List<String> flows = new ArrayList<>();
            for (Address target : instruction.getFlows()) flows.add(target.toString());
            item.put("flows", flows);
            if (instruction.getFallThrough() != null) item.put("fallthrough", instruction.getFallThrough().toString());
            instructions.add(item);
        }
        report.put("instructions", instructions);
        Files.writeString(Paths.get(args[0]), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
        println("DSP_INSPECT ranges=" + ranges.size() + " instructions=" + instructions.size());
    }
}
