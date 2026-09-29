// Isolate imported local/signature type problems; changes are always rolled back.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.data.*;
import ghidra.program.model.symbol.SourceType;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRetryUntyped extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        boolean noWide = args.length > 2 && args[2].equals("no-wide-simplification");
        Path output = Paths.get(args[0]).resolve(noWide ? "untyped-no-wide" : "untyped");
        Files.createDirectories(output);
        Function function = getFunctionAt(toAddr(args[1]));
        if (function == null) throw new IllegalArgumentException("Function not found");
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("address", args[1]);
        report.put("original_signature", function.getSignature().toString());
        report.put("wide_simplification_disabled", noWide);
        List<String> locals = new ArrayList<>();
        for (Variable variable : function.getLocalVariables()) locals.add(variable.toString());
        report.put("removed_local_types", locals);
        int transaction = currentProgram.startTransaction("Temporary untyped decompilation");
        try {
            for (Variable variable : function.getLocalVariables()) function.removeVariable(variable);
            // Width-preserving integer types prevent struct-handle pointer inference.
            for (Parameter parameter : function.getParameters()) {
                parameter.setDataType(Undefined.getUndefinedDataType(parameter.getLength()), SourceType.ANALYSIS);
            }
            int length = function.getReturnType().getLength();
            if (length > 0) function.setReturnType(Undefined.getUndefinedDataType(length), SourceType.ANALYSIS);
            report.put("temporary_signature", function.getSignature().toString());
            DecompInterface decompiler = new DecompInterface();
            if (noWide) {
                DecompileOptions options = new DecompileOptions();
                options.setSimplifyDoublePrecision(false);
                options.setSplitStructures(false);
                options.setSplitArrays(false);
                options.setSplitPointers(false);
                decompiler.setOptions(options);
            }
            decompiler.toggleSyntaxTree(false);
            if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
            try {
                DecompileResults result = decompiler.decompileFunction(function, 120, monitor);
                report.put("message", result.getErrorMessage());
                boolean success = result.getDecompiledFunction() != null;
                report.put("status", success ? "decompiled" : result.isTimedOut() ? "timeout" : "failed");
                if (success) {
                    String name = args[1] + ".c";
                    Files.writeString(output.resolve(name), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                    report.put("file", name);
                }
                println("DSP_UNTYPED " + args[1] + " " + report.get("status"));
            } finally { decompiler.dispose(); }
        } finally { currentProgram.endTransaction(transaction, false); }
        report.put("program_changes_rolled_back", true);
        Files.writeString(output.resolve(args[1] + ".json"), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
    }
}
