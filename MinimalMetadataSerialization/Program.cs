using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MinimalMetadataSerialization
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            // Get this assembly path
            var rootPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Define our assembly name, root namespace and physical path
            var assemblyName = "SampleLib";
            var rootNamespace = "SampleLib";
            var path = Path.Combine(rootPath, $"{assemblyName}.dll");

            // Serialize our helper library in target path
            SerializeHelperLibrary(assemblyName, rootNamespace, path);

            // Now, load from disk and load relevant metadata for reflection
            var asm = Assembly.LoadFile(path);
            var testClassType = asm.GetType($"{rootNamespace}.TestClass");
            var testMethodInfo = testClassType.GetMethod("TestMethod");

            // Create an instance of TestClass and invoke TestClass::TestMethod instance method
            var testClassInstance = Activator.CreateInstance(testClassType);
            testMethodInfo.Invoke(testClassInstance, new object[] { });
        }

        private static void SerializeHelperLibrary(string assemblyName, string rootNamespace, string path)
        {
            var ilBuilder = new BlobBuilder();
            var metadataBuilder = new MetadataBuilder();

            EmitHelperLibraryMetadata(assemblyName, rootNamespace, metadataBuilder, ilBuilder);

            // Following code does not work:
            //
            //   var peHeaderBuilder = PEHeaderBuilder.CreateLibraryHeader();
            //
            // PEHeaderBuilder.CreateLibraryHeader() method does not set Characteristics.ExecutableImage bit.
            // If we serialize an assembly without setting it, .NET Core runtime refuses to load.
            // So, we explicitly set it by using PEHeaderBuilder constructor.

            var peHeaderBuilder =
                new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);
            var peBuilder = new ManagedPEBuilder(peHeaderBuilder, new MetadataRootBuilder(metadataBuilder), ilBuilder);

            var peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);

            using (var stream = File.Create(path))
            {
                peBlob.WriteContentTo(stream);
            }
        }

        private static void EmitHelperLibraryMetadata(string assemblyName, string rootNamespace,
            MetadataBuilder metadata,
            BlobBuilder ilBuilder)
        {
            // Dynamically get mscorlib assembly information for executing runtime
            var mscorlibName = typeof(object).GetTypeInfo().Assembly.GetName();

            // Create module version identifier
            var mvid = Guid.NewGuid();

            metadata.AddModule(
                0,
                metadata.GetOrAddString($"{assemblyName}.dll"),
                metadata.GetOrAddGuid(mvid),
                default(GuidHandle),
                default(GuidHandle));

            metadata.AddAssembly(
                metadata.GetOrAddString(assemblyName),
                new Version(1, 0, 0, 0),
                default(StringHandle),
                default(BlobHandle),
                default(AssemblyFlags),
                AssemblyHashAlgorithm.Sha1);

            // Add mscorlib assembly reference and related type references
            var mscorlibRef = metadata.AddAssemblyReference(
                metadata.GetOrAddString("mscorlib"),
                mscorlibName.Version,
                default(StringHandle),
                metadata.GetOrAddBlob(mscorlibName.GetPublicKeyToken()),
                default(AssemblyFlags),
                default(BlobHandle));

            var objectTypeRef = metadata.AddTypeReference(
                mscorlibRef,
                metadata.GetOrAddString("System"),
                metadata.GetOrAddString("Object"));

            var consoleTypeRef = metadata.AddTypeReference(
                mscorlibRef,
                metadata.GetOrAddString("System"),
                metadata.GetOrAddString("Console"));

            // Create method signature of System.Console.WriteLine reference
            var consoleWriteLineSignature = new BlobBuilder();

            new BlobEncoder(consoleWriteLineSignature).MethodSignature().Parameters(1,
                returnType => returnType.Void(),
                parameters => parameters.AddParameter().Type().String());

            var consoleWriteLineMemberRef = metadata.AddMemberReference(
                consoleTypeRef,
                metadata.GetOrAddString("WriteLine"),
                metadata.GetOrAddBlob(consoleWriteLineSignature));

            // Create constructor signature for TestClass
            var parameterlessCtorSignature = new BlobBuilder();

            new BlobEncoder(parameterlessCtorSignature).MethodSignature(isInstanceMethod: true)
                .Parameters(0, returnType => returnType.Void(), parameters => { });

            var parameterlessCtorBlobIndex = metadata.GetOrAddBlob(parameterlessCtorSignature);

            // Create constructor of TestClass
            var objectCtorMemberRef = metadata.AddMemberReference(
                objectTypeRef,
                metadata.GetOrAddString(".ctor"),
                parameterlessCtorBlobIndex);

            // Create TestClass.TestMethod signature
            var testMethodSignature = new BlobBuilder();

            new BlobEncoder(testMethodSignature).MethodSignature(isInstanceMethod: true)
                .Parameters(0, returnType => returnType.Void(), parameters => { });


            // Create an IL stream and serialize each methods
            var methodBodyStream = new MethodBodyStreamEncoder(ilBuilder);

            var codeBuilder = new BlobBuilder();
            InstructionEncoder il;

            // Create constructor body by composing IL codes

            // TestClass::.ctor
            il = new InstructionEncoder(codeBuilder);

            // ldarg.0
            il.LoadArgument(0);

            // call instance void [mscorlib]System.Object::.ctor()
            il.Call(objectCtorMemberRef);

            // ret
            il.OpCode(ILOpCode.Ret);

            var ctorBodyOffset = methodBodyStream.AddMethodBody(il);
            codeBuilder.Clear();

            // Create TestClass.TestMethod body by composing IL codes

            // TestClass::TestMethod
            il = new InstructionEncoder(codeBuilder);

            // ldstr "hello"
            il.LoadString(metadata.GetOrAddUserString("Hello world from serialized assembly!"));

            // call void [mscorlib]System.Console::WriteLine(string)
            il.Call(consoleWriteLineMemberRef);

            // ret
            il.OpCode(ILOpCode.Ret);

            var testMethodBodyOffset = methodBodyStream.AddMethodBody(il);

            // Create TestClass.TestMethod method definition
            var testMethodDef = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                metadata.GetOrAddString("TestMethod"),
                metadata.GetOrAddBlob(testMethodSignature),
                testMethodBodyOffset,
                default(ParameterHandle));

            // Create TestClass constructor definition
            var ctorDef = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                metadata.GetOrAddString(".ctor"),
                parameterlessCtorBlobIndex,
                ctorBodyOffset,
                default(ParameterHandle));

            metadata.AddTypeDefinition(
                default(TypeAttributes),
                default(StringHandle),
                metadata.GetOrAddString("<Module>"),
                default(EntityHandle),
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(1));

            metadata.AddTypeDefinition(
                TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.AutoLayout |
                TypeAttributes.BeforeFieldInit,
                metadata.GetOrAddString(rootNamespace),
                metadata.GetOrAddString("TestClass"),
                objectTypeRef,
                MetadataTokens.FieldDefinitionHandle(1),
                testMethodDef);
        }
    }
}