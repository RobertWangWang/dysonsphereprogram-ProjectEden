// Configure a local, identity-verified PDB before normal automatic analysis.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.plugin.core.analysis.PdbUniversalAnalyzer;
import java.io.File;

public class DspLoadPdb extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 1) throw new IllegalArgumentException("Expected PDB filename");
        File pdb = new File(args[0]);
        if (!pdb.isFile()) throw new IllegalArgumentException("Missing PDB: " + pdb);
        PdbUniversalAnalyzer.setPdbFileOption(currentProgram, pdb);
        println("DSP_PDB configured: " + pdb.getAbsolutePath());
    }
}
