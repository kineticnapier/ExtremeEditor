using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ExtremeEditor.Audio;

namespace ExtremeEditor.Audio.Tests;

internal static class RenderOutsideCacheLockRegression
{
    [ModuleInitializer]
    public static void Run()
    {
        Type providerType = typeof(SampleAccurateHitSoundProvider);
        AssertRenderCallOutsideMonitorProtectedRegion(providerType, "EnsureChunk");
        AssertRenderCallOutsideMonitorProtectedRegion(providerType, "TryEnsureChunkWithinBudget");
    }

    private static void AssertRenderCallOutsideMonitorProtectedRegion(Type providerType, string methodName)
    {
        MethodInfo method = providerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{methodName} is unavailable.");
        MethodBody body = method.GetMethodBody()
            ?? throw new InvalidOperationException($"{methodName} has no method body.");
        byte[] il = body.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{methodName} has no IL body.");

        IReadOnlyList<Instruction> instructions = Decode(method.Module, il);
        int renderCallOffset = instructions
            .Where(instruction => instruction.Method?.Name == "RenderChunkMeasured")
            .Select(instruction => instruction.Offset)
            .DefaultIfEmpty(-1)
            .First();
        if (renderCallOffset < 0)
            throw new InvalidOperationException($"{methodName} does not call RenderChunkMeasured.");

        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            if (clause.Flags != ExceptionHandlingClauseOptions.Finally)
                continue;

            int handlerStart = clause.HandlerOffset;
            int handlerEnd = handlerStart + clause.HandlerLength;
            bool exitsMonitor = instructions.Any(instruction =>
                instruction.Offset >= handlerStart &&
                instruction.Offset < handlerEnd &&
                instruction.Method?.DeclaringType == typeof(Monitor) &&
                instruction.Method.Name == nameof(Monitor.Exit));
            if (!exitsMonitor)
                continue;

            int tryStart = clause.TryOffset;
            int tryEnd = tryStart + clause.TryLength;
            if (renderCallOffset >= tryStart && renderCallOffset < tryEnd)
            {
                throw new InvalidOperationException(
                    $"RED: {methodName} calls RenderChunkMeasured while _cacheLock is held; expensive PCM rendering must occur outside the Monitor-protected region.");
            }
        }
    }

    private static IReadOnlyList<Instruction> Decode(Module module, byte[] il)
    {
        var result = new List<Instruction>();
        int offset = 0;
        while (offset < il.Length)
        {
            int instructionOffset = offset;
            OpCode opcode;
            byte first = il[offset++];
            if (first == 0xFE)
            {
                if (offset >= il.Length)
                    throw new InvalidOperationException("truncated two-byte IL opcode");
                opcode = TwoByteOpcodes[il[offset++]];
            }
            else
            {
                opcode = OneByteOpcodes[first];
            }

            MethodBase? calledMethod = null;
            int operandSize = GetOperandSize(opcode.OperandType, il, offset);
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, offset);
                try
                {
                    calledMethod = module.ResolveMethod(token);
                }
                catch (ArgumentException)
                {
                    // Not relevant to this structural regression.
                }
            }

            result.Add(new Instruction(instructionOffset, calledMethod));
            offset += operandSize;
        }

        return result;
    }

    private static int GetOperandSize(OperandType type, byte[] il, int operandOffset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI => 4,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, operandOffset) * 4,
        _ => throw new NotSupportedException($"Unsupported IL operand type: {type}")
    };

    private static readonly OpCode[] OneByteOpcodes = BuildOpcodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpcodes = BuildOpcodeTable(twoByte: true);

    private static OpCode[] BuildOpcodeTable(bool twoByte)
    {
        var table = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
                continue;

            ushort value = unchecked((ushort)opcode.Value);
            bool isTwoByte = (value & 0xFF00) == 0xFE00;
            if (isTwoByte != twoByte)
                continue;

            table[value & 0xFF] = opcode;
        }
        return table;
    }

    private readonly record struct Instruction(int Offset, MethodBase? Method);
}
