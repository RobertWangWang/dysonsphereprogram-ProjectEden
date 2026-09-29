// Retry failed functions against the saved program without changing its analysis.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.Function;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.*;

public class DspRetryNative extends GhidraScript {
    @Override public void run() throws Exception {
        Path base = Paths.get(getScriptArgs()[0]);
        JsonObject previous = JsonParser.parseString(Files.readString(base.resolve("functions.json"))).getAsJsonObject();
        Path output = base.resolve("retry");
        Files.createDirectories(output);
        List<Map<String,Object>> records = new ArrayList<>();
        for (JsonElement element : previous.getAsJsonArray("functions")) {
            JsonObject old = element.getAsJsonObject();
            String status = old.get("status").getAsString();
            if (status.equals("decompiled") || status.equals("external")) continue;
            String address = old.get("address").getAsString();
            Function function = getFunctionAt(toAddr(address));
            if (function == null) throw new IllegalStateException("Missing function " + address);
            for (int mode = 0; mode < 3; mode++) {
                DecompInterface decompiler = new DecompInterface();
                DecompileOptions options = new DecompileOptions();
                if (mode >= 1) options.setBitfieldAccess(false);
                if (mode >= 2) {
                    options.setEliminateUnreachable(false);
                    options.setRespectReadOnly(false);
                    options.setInferConstantPointers(false);
                }
                decompiler.setOptions(options);
                boolean success = false;
                Map<String,Object> record = new LinkedHashMap<>();
                record.put("address", address);
                record.put("name", function.getName(true));
                record.put("mode", mode);
                try {
                    if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
                    DecompileResults result = decompiler.decompileFunction(function, 300, monitor);
                    success = result.decompileCompleted() && result.getDecompiledFunction() != null;
                    record.put("message", result.getErrorMessage());
                    record.put("status", success ? "decompiled" : result.isTimedOut() ? "timeout" : "failed");
                    if (success) {
                        String filename = address.replace(':', '_') + "-mode" + mode + ".c";
                        Files.writeString(output.resolve(filename), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                        record.put("file", filename);
                    }
                } finally { decompiler.dispose(); }
                records.add(record);
                println("DSP_RETRY " + address + " mode=" + mode + " " + record.get("status"));
                if (success) break;
            }
        }
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("timeout_seconds", 300);
        report.put("modes", Arrays.asList("default", "bitfield access disabled", "bitfield/unreachable/read-only/infer-constant-pointers disabled"));
        report.put("attempts", records);
        Files.writeString(output.resolve("attempts.json"), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
    }
}
