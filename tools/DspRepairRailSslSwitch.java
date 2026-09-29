// Restore the observed absolute jump table in the original enclosing function.
// @category DSP.Knowledge
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.pcode.JumpTable;
import java.nio.file.*;
import java.util.*;
import com.google.gson.GsonBuilder;

public class DspRepairRailSslSwitch extends GhidraScript {
    @Override public void run() throws Exception {
        if(!currentProgram.getExecutableSHA256().equalsIgnoreCase("c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1"))throw new IllegalStateException("Wrong binary");
        Path out=Paths.get(getScriptArgs()[0]);Files.createDirectories(out);
        Address entry=toAddr("105245a0"),end=toAddr("10524ea9"),branch=toAddr("10524897"),table=toAddr("10524ecc");
        long[] expected={0x105248a6L,0x105248a6L,0x10524e5aL,0x1052489eL,0x105248aeL,0x105248aeL,0x105248aeL,0x105248aeL};
        if(!getInstructionAt(branch).toString().equals("JMP dword ptr [EAX*0x4 + 0x10524ecc]"))throw new IllegalStateException("Branch differs");
        Map<String,Object> report=new LinkedHashMap<>();report.put("source_sha256",currentProgram.getExecutableSHA256());report.put("address",entry.toString());
        int tx=currentProgram.startTransaction("Restore enclosing SSL switch");DecompInterface d=new DecompInterface();
        try {
            Function f=getFunctionAt(entry);if(f==null)throw new IllegalStateException("Missing parent");
            List<String> targets=new ArrayList<>();
            for(int n=0;n<8;n++) {
                long pointer=Integer.toUnsignedLong(getInt(table.add(n*4)));if(pointer!=expected[n])throw new IllegalStateException("Table differs");
                Address target=toAddr(pointer);targets.add(target.toString());disassemble(target);
                currentProgram.getReferenceManager().addMemoryReference(branch,target,RefType.COMPUTED_JUMP,SourceType.USER_DEFINED,-1);
            }
            f.setBody(new AddressSet(entry,end));
            ArrayList<Address> ordered=new ArrayList<>();for(long value:expected)ordered.add(toAddr(value));
            new JumpTable(branch,ordered,true,0).writeOverride(f);report.put("manual_jump_override",true);
            StringBuilder asm=new StringBuilder();int count=0;
            for(Instruction i:currentProgram.getListing().getInstructions(f.getBody(),true)) {asm.append(i.getAddress()).append(" ").append(HexFormat.of().formatHex(i.getBytes())).append(" ").append(i).append("\n");count++;}
            Files.writeString(out.resolve("105245a0.asm"),asm);report.put("instruction_count",count);report.put("body_bytes",f.getBody().getNumAddresses());report.put("end",end.toString());report.put("targets",targets);
            d.toggleSyntaxTree(false);if(!d.openProgram(currentProgram))throw new IllegalStateException(d.getLastMessage());
            DecompileResults result=d.decompileFunction(f,180,monitor);report.put("message",result.getErrorMessage());report.put("status",result.getDecompiledFunction()==null?"failed":"decompiled");
            if(result.getDecompiledFunction()!=null) {String code=result.getDecompiledFunction().getC();Files.writeString(out.resolve("105245a0.c"),code);report.put("warnings",code.lines().filter(s->s.contains("WARNING")).toList());}
        } finally {d.dispose();currentProgram.endTransaction(tx,false);}
        report.put("program_changes_rolled_back",true);Files.writeString(out.resolve("report.json"),new GsonBuilder().setPrettyPrinting().create().toJson(report));println("RAIL_SSL_SWITCH "+report.get("status"));
    }
}
