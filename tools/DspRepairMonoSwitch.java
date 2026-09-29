// Recover the RVA switch table verified in this exact Mono binary.
// All Ghidra edits are temporary; no game bytes are changed.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairMonoSwitch extends GhidraScript {
    @Override public void run() throws Exception {
        if (!currentProgram.getExecutableSHA256().equalsIgnoreCase("6f9713406d52d55a669db35fe8730e816e424cb60de3a4fd903d569de6721475")) throw new IllegalStateException("Unexpected binary");
        boolean normalize=getScriptArgs().length>1 && getScriptArgs()[1].equals("normalize");
        Path output = Paths.get(getScriptArgs()[0]).resolve(normalize ? "switch-normalize" : "switch-repair");
        Files.createDirectories(output);
        Address entry = toAddr("1802b5740"), end = toAddr("180312691");
        Address branch = toAddr("1802bc991"), table = toAddr("180312694");
        if ((getByte(branch) & 255) != 255 || (getByte(branch.add(1)) & 255) != 224) throw new IllegalStateException("Unexpected jump instruction");
        if (getInt(toAddr("1802bc96f")) != 0x147) throw new IllegalStateException("Unexpected switch upper bound");
        Function function = getFunctionAt(entry);
        Map<String,Object> report = new LinkedHashMap<>();
        report.put("source_sha256", currentProgram.getExecutableSHA256());
        report.put("address", entry.toString());
        report.put("name", function.getName(true));
        report.put("simplification_style",normalize ? "normalize" : "decompile");
        report.put("old_body_bytes", function.getBody().getNumAddresses());
        report.put("table_address", table.toString());
        List<String> targets = new ArrayList<>();
        AddressSet starts = new AddressSet();
        for (int i = 0; i < 328; i++) {
            Address target = currentProgram.getImageBase().add(Integer.toUnsignedLong(getInt(table.add(i * 4))));
            if (target.compareTo(entry) < 0 || target.compareTo(end) > 0) throw new IllegalStateException("Target outside PDB function extent");
            targets.add(target.toString());
            starts.add(target);
        }
        report.put("table_targets", targets);
        Address nestedBranch = toAddr("1802ebae3"), nestedTable = toAddr("180312bb4");
        if ((getByte(nestedBranch) & 255) != 255 || (getByte(nestedBranch.add(1)) & 255) != 224 || (getByte(toAddr("1802ebac4")) & 255) != 0x1b) throw new IllegalStateException("Unexpected nested switch");
        AddressSet nestedStarts = new AddressSet();
        List<String> nestedTargets = new ArrayList<>();
        for (int i = 0; i < 28; i++) {
            Address target = currentProgram.getImageBase().add(Integer.toUnsignedLong(getInt(nestedTable.add(i * 4))));
            if (target.compareTo(entry) < 0 || target.compareTo(end) > 0) throw new IllegalStateException("Nested target outside function extent");
            nestedStarts.add(target);
            nestedTargets.add(target.toString());
        }
        report.put("nested_table_address", nestedTable.toString());
        report.put("nested_table_targets", nestedTargets);
        int transaction = currentProgram.startTransaction("Verified Mono switch recovery");
        try {
            for (Address target : starts.getAddresses(true)) currentProgram.getReferenceManager().addMemoryReference(branch, target, RefType.COMPUTED_JUMP, SourceType.USER_DEFINED, -1);
            for (Address target : nestedStarts.getAddresses(true)) currentProgram.getReferenceManager().addMemoryReference(nestedBranch, target, RefType.COMPUTED_JUMP, SourceType.USER_DEFINED, -1);
            starts.add(nestedStarts);
            DisassembleCommand command = new DisassembleCommand(starts, new AddressSet(entry, end), true);
            if (!command.applyTo(currentProgram, monitor)) throw new IllegalStateException(command.getStatusMsg());
            for (Address target : starts.getAddresses(true)) if (getInstructionAt(target) == null) throw new IllegalStateException("Undecoded target " + target);
            AddressSetView body = CreateFunctionCmd.getFunctionBody(currentProgram, entry, false, monitor);
            function.setBody(body);
            report.put("new_body_bytes", body.getNumAddresses());
            List<List<String>> ranges = new ArrayList<>();
            for (AddressRange range : body.getAddressRanges()) ranges.add(Arrays.asList(range.getMinAddress().toString(), range.getMaxAddress().toString()));
            report.put("ranges", ranges);
            List<String> unresolved = new ArrayList<>();
            int count = 0;
            StringBuilder assembly=new StringBuilder();
            for (Instruction instruction : currentProgram.getListing().getInstructions(body, true)) {
                count++;
                if(normalize)assembly.append(instruction.getAddress()).append(" ").append(HexFormat.of().formatHex(instruction.getBytes())).append(" ").append(instruction).append("\n");
                if (instruction.getFlowType().isComputed() && instruction.getFlowType().isJump() && instruction.getFlows().length == 0) unresolved.add(instruction.getAddress().toString());
            }
            report.put("instructions", count);
            if(normalize)Files.writeString(output.resolve("mono_method_to_ir.asm"),assembly.toString(),StandardCharsets.UTF_8);
            report.put("unresolved_computed_jumps", unresolved);
            report.put("status", "decompiling");
            write(output, report);
            println("DSP_SWITCH body=" + body.getNumAddresses() + " instructions=" + count + " unresolved_jumps=" + unresolved.size());
            if (!unresolved.isEmpty()) throw new IllegalStateException("Unresolved jump tables remain: " + unresolved);
            DecompInterface decompiler = new DecompInterface();
            DecompileOptions options = new DecompileOptions();
            options.setMaxInstructions(200000);
            options.setMaxPayloadMBytes(128);
            decompiler.setOptions(options);
            if(normalize)decompiler.setSimplificationStyle("normalize");
            decompiler.toggleSyntaxTree(false);
            if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
            try {
                DecompileResults result = decompiler.decompileFunction(function, normalize ? 120 : 900, monitor);
                boolean success = result.getDecompiledFunction() != null;
                report.put("status", success ? "decompiled" : result.isTimedOut() ? "timeout" : "failed");
                report.put("message", result.getErrorMessage());
                if (success) {
                    Files.writeString(output.resolve("mono_method_to_ir.c"), result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                    report.put("file", "mono_method_to_ir.c");
                }
            } finally { decompiler.dispose(); }
        } finally { currentProgram.endTransaction(transaction, false); }
        report.put("program_changes_rolled_back", true);
        write(output, report);
        println("DSP_SWITCH_FINISHED " + report.get("status"));
    }
    private void write(Path output, Map<String,Object> report) throws Exception {
        Files.writeString(output.resolve("report.json"), new GsonBuilder().setPrettyPrinting().create().toJson(report), StandardCharsets.UTF_8);
    }
}
