using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ExtremeEditor.AssetIndexer;

internal static class IlInspectCommand
{
    public static bool HasOption(string[] args)
        => args.Any(static arg => string.Equals(arg, "--inspect-il", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            string gameRoot = ResolveGameRoot(options.GameRoot);
            string managedDirectory = Path.Combine(gameRoot, "A Dance of Fire and Ice_Data", "Managed");
            string assemblyPath = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Assembly-CSharp.dll not found", assemblyPath);

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDirectory);
            var readerParameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false,
                InMemory = true
            };

            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParameters);
            List<TypeDefinition> candidates = EnumerateTypes(assembly.MainModule.Types)
                .Where(type =>
                    string.Equals(type.FullName, options.TypeName, StringComparison.Ordinal) ||
                    string.Equals(type.Name, options.TypeName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
                throw new InvalidOperationException($"Type '{options.TypeName}' was not found in Assembly-CSharp.dll.");

            TypeDefinition? exact = candidates.FirstOrDefault(type =>
                string.Equals(type.FullName, options.TypeName, StringComparison.Ordinal));
            if (exact is null && candidates.Count > 1)
            {
                string names = string.Join(", ", candidates.Select(static type => type.FullName));
                throw new InvalidOperationException(
                    $"Type name '{options.TypeName}' is ambiguous. Use the full name. Candidates: {names}");
            }

            TypeDefinition target = exact ?? candidates[0];
            Console.WriteLine($"[il] game={gameRoot}");
            Console.WriteLine($"[il] assembly={Path.GetRelativePath(gameRoot, assemblyPath)}");
            Console.WriteLine($"[il] type={target.FullName} base={target.BaseType?.FullName ?? "<none>"}");
            if (options.MethodFilter is not null)
                Console.WriteLine($"[il] method-filter={options.MethodFilter}");
            Console.WriteLine($"[il] fields={target.Fields.Count} methods={target.Methods.Count}");
            Console.WriteLine();

            Console.WriteLine("FIELDS");
            foreach (FieldDefinition field in target.Fields)
            {
                string modifiers = JoinModifiers(
                    field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsPrivate ? "private" : "internal",
                    field.IsStatic ? "static" : null,
                    field.IsInitOnly ? "readonly" : null);
                string constant = field.HasConstant ? $" = {FormatOperand(field.Constant)}" : string.Empty;
                Console.WriteLine($"  {modifiers} {field.FieldType.FullName} {field.Name}{constant}");
            }

            IEnumerable<MethodDefinition> methods = target.Methods;
            if (!string.IsNullOrWhiteSpace(options.MethodFilter))
            {
                methods = methods.Where(method =>
                    method.Name.Contains(options.MethodFilter, StringComparison.OrdinalIgnoreCase));
            }

            int emittedMethods = 0;
            foreach (MethodDefinition method in methods)
            {
                emittedMethods++;
                Console.WriteLine();
                Console.WriteLine($"METHOD {method.FullName}");
                Console.WriteLine($"  token={method.MetadataToken} static={method.IsStatic} virtual={method.IsVirtual} hasBody={method.HasBody}");
                if (!method.HasBody)
                    continue;

                MethodBody body = method.Body;
                Console.WriteLine($"  maxStack={body.MaxStackSize} initLocals={body.InitLocals} locals={body.Variables.Count}");
                foreach (VariableDefinition variable in body.Variables)
                    Console.WriteLine($"  .local V_{variable.Index}: {variable.VariableType.FullName}");

                foreach (Instruction instruction in body.Instructions)
                {
                    string operand = instruction.Operand is null ? string.Empty : " " + FormatOperand(instruction.Operand);
                    Console.WriteLine($"    IL_{instruction.Offset:X4}: {instruction.OpCode}{operand}");
                }

                if (body.ExceptionHandlers.Count > 0)
                {
                    Console.WriteLine("  exception-handlers:");
                    foreach (ExceptionHandler handler in body.ExceptionHandlers)
                    {
                        Console.WriteLine(
                            $"    {handler.HandlerType} try=IL_{OffsetOf(handler.TryStart):X4}-IL_{OffsetOf(handler.TryEnd):X4} " +
                            $"handler=IL_{OffsetOf(handler.HandlerStart):X4}-IL_{OffsetOf(handler.HandlerEnd):X4} " +
                            $"catch={handler.CatchType?.FullName ?? "<none>"}");
                    }
                }
            }

            if (emittedMethods == 0)
            {
                Console.WriteLine();
                Console.WriteLine("[il] no methods matched the requested filter.");
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"inspect-il: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"inspect-il fatal: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition type in roots)
        {
            yield return type;
            foreach (TypeDefinition nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static string FormatOperand(object? operand)
    {
        return operand switch
        {
            null => string.Empty,
            string text => JsonSerializer.Serialize(text),
            Instruction target => $"IL_{target.Offset:X4}",
            Instruction[] targets => string.Join(", ", targets.Select(static target => $"IL_{target.Offset:X4}")),
            MethodReference method => method.FullName,
            FieldReference field => field.FullName,
            TypeReference type => type.FullName,
            ParameterDefinition parameter => parameter.Name.Length == 0 ? $"arg{parameter.Index}" : parameter.Name,
            VariableDefinition variable => $"V_{variable.Index}",
            _ => operand.ToString() ?? string.Empty
        };
    }

    private static int OffsetOf(Instruction? instruction)
        => instruction?.Offset ?? -1;

    private static string JoinModifiers(params string?[] modifiers)
        => string.Join(" ", modifiers.Where(static modifier => !string.IsNullOrEmpty(modifier))!);

    private static string ResolveGameRoot(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return ValidateGameRoot(explicitPath);

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "A Dance of Fire and Ice"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "A Dance of Fire and Ice")
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "A Dance of Fire and Ice_Data")))
                return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException(
            "ADOFAI installation was not found automatically. Pass --adofai <game directory>.");
    }

    private static string ValidateGameRoot(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        if (!Directory.Exists(Path.Combine(fullPath, "A Dance of Fire and Ice_Data")))
            throw new DirectoryNotFoundException($"Not an ADOFAI installation directory: {fullPath}");
        return fullPath;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("IL inspect usage:");
        Console.WriteLine("  dotnet run --project src/ExtremeEditor.AssetIndexer -- --adofai <dir> --inspect-il FloorMesh [--method <substring>]");
    }

    private sealed record Options(string? GameRoot, string TypeName, string? MethodFilter)
    {
        public static Options Parse(string[] args)
        {
            string? gameRoot = null;
            string? typeName = null;
            string? methodFilter = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--adofai":
                        gameRoot = RequireValue(args, ref i, arg);
                        break;
                    case "--inspect-il":
                        typeName = RequireValue(args, ref i, arg);
                        break;
                    case "--method":
                        methodFilter = RequireValue(args, ref i, arg);
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown inspect-il option: {arg}");
                        if (gameRoot is not null)
                            throw new ArgumentException($"Unexpected positional argument: {arg}");
                        gameRoot = arg;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Missing --inspect-il <type-name>.");

            return new Options(gameRoot, typeName, methodFilter);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}");
            return args[++index];
        }
    }
}
