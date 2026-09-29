// Export each discovered native function; run inside Ghidra headless, never the game.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspExportNative extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 1) throw new IllegalArgumentException("Expected output directory");
        Path output = Paths.get(args[0]);
        Files.createDirectories(output.resolve("functions"));
        Path marker = output.resolve("functions.json");
        Files.deleteIfExists(marker);
        DecompInterface decompiler = new DecompInterface();
        if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
        List<Map<String,Object>> records = new ArrayList<>();
        int ok = 0, failed = 0, external = 0;
        try {
            FunctionIterator functions = currentProgram.getFunctionManager().getFunctions(true);
            while (functions.hasNext()) {
                monitor.checkCancelled();
                Function function = functions.next();
                Map<String,Object> record = new LinkedHashMap<>();
                String address = function.getEntryPoint().toString();
                record.put("address", address);
                record.put("name", function.getName(true));
                record.put("signature", function.getSignature().toString());
                record.put("size", function.getBody().getNumAddresses());
                record.put("thunk", function.isThunk());
                if (function.isExternal()) {
                    record.put("status", "external"); external++;
                } else {
                    DecompileResults result = decompiler.decompileFunction(function, 60, monitor);
                    String error = result.getErrorMessage();
                    record.put("message", error == null ? "" : error);
                    if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                        String filename = "functions/" + address.replace(':', '_') + ".c";
                        Files.writeString(output.resolve(filename), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                        record.put("file", filename);
                        record.put("status", "decompiled"); ok++;
                    } else {
                        record.put("status", result.isTimedOut() ? "timeout" : "failed"); failed++;
                    }
                }
                records.add(record);
                if (records.size() % 100 == 0) println("DSP_NATIVE " + currentProgram.getName() + " processed=" + records.size() + " ok=" + ok + " failed=" + failed);
            }
        } finally { decompiler.dispose(); }
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("program", currentProgram.getName());
        report.put("sha256", currentProgram.getExecutableSHA256());
        report.put("language", currentProgram.getLanguageID().toString());
        report.put("image_base", currentProgram.getImageBase().toString());
        report.put("discovered", records.size());
        report.put("decompiled", ok);
        report.put("failed", failed);
        report.put("external", external);
        report.put("functions", records);
        Files.writeString(marker, new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
        println("DSP_NATIVE_FINISHED " + currentProgram.getName() + " discovered=" + records.size() + " ok=" + ok + " failed=" + failed);
    }
}
