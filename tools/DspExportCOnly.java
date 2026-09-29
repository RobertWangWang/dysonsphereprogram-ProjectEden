// Retry failed functions without marshaling the optional high-level syntax tree.
// The saved program, signatures and analysis are not modified.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.*;

public class DspExportCOnly extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        Path base = Paths.get(args[0]);
        String style = args.length > 1 ? args[1] : "decompile";
        if (!Arrays.asList("decompile", "normalize", "register", "firstpass").contains(style)) throw new IllegalArgumentException("Unsupported simplification style");
        JsonObject previous = JsonParser.parseString(Files.readString(base.resolve("functions.json"))).getAsJsonObject();
        Path output = base.resolve(style.equals("decompile") ? "c-only" : "c-only-" + style);
        Files.createDirectories(output);
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("syntax_tree", false);
        report.put("simplification_style", style);
        report.put("timeout_seconds", 60);
        List<Map<String,Object>> records = new ArrayList<>();
        report.put("attempts", records);
        DecompInterface decompiler = new DecompInterface();
        decompiler.setSimplificationStyle(style);
        decompiler.toggleSyntaxTree(false);
        decompiler.toggleCCode(true);
        if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
        try {
            for (JsonElement element : previous.getAsJsonArray("functions")) {
                JsonObject old = element.getAsJsonObject();
                String status = old.get("status").getAsString();
                if (status.equals("decompiled") || status.equals("external")) continue;
                String address = old.get("address").getAsString();
                Function function = getFunctionAt(toAddr(address));
                if (function == null) throw new IllegalStateException("Missing function " + address);
                Map<String,Object> record = new LinkedHashMap<>();
                record.put("address", address);
                record.put("name", function.getName(true));
                DecompileResults result = decompiler.decompileFunction(function, 60, monitor);
                boolean success = result.getDecompiledFunction() != null;
                record.put("message", result.getErrorMessage());
                record.put("status", success ? "decompiled" : result.isTimedOut() ? "timeout" : "failed");
                if (success) {
                    String filename = address.replace(':', '_') + ".c";
                    Files.writeString(output.resolve(filename), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                    record.put("file", filename);
                }
                records.add(record);
                Files.writeString(output.resolve("attempts.json"), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
                println("DSP_CONLY " + address + " " + record.get("status"));
            }
        } finally { decompiler.dispose(); }
        report.put("finished", true);
        Files.writeString(output.resolve("attempts.json"), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
    }
}
