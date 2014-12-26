﻿namespace RazorEngine.Compilation
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security;
    using System.Security.Permissions;
    using System.Text;
    using System.Web.Razor;
    using System.Web.Razor.Parser;
    using Templating;

    /// <summary>
    /// Provides a base implementation of a direct compiler service.
    /// </summary>
    public abstract class DirectCompilerServiceBase : CompilerServiceBase, IDisposable
    {
        #region Fields
        private readonly CodeDomProvider _codeDomProvider;
        private bool _disposed;
        #endregion

        #region Constructor
        /// <summary>
        /// Initialises a new instance of <see cref="DirectCompilerServiceBase"/>.
        /// </summary>
        /// <param name="codeLanguage">The razor code language.</param>
        /// <param name="codeDomProvider">The code dom provider used to generate code.</param>
        /// <param name="markupParserFactory">The markup parser factory.</param>
        [SecurityCritical]
        protected DirectCompilerServiceBase(RazorCodeLanguage codeLanguage, CodeDomProvider codeDomProvider, Func<ParserBase> markupParserFactory)
            : base(codeLanguage, new ParserBaseCreator(markupParserFactory))
        {
            _codeDomProvider = codeDomProvider;
        }
        #endregion
        
        #region Methods

        /// <summary>
        /// Tries to create and return a unique temporary directory.
        /// </summary>
        /// <returns>the (already created) temporary directory</returns>
        public static string GetTemporaryDirectory()
        {
            var created = false;
            var tried = 0;
            string tempDirectory = "";
            while (!created && tried < 10)
            {
                tried++;
                try
                {
                    tempDirectory = Path.Combine(Path.GetTempPath(), "RazorEngine_" + Path.GetRandomFileName());
                    if (!Directory.Exists(tempDirectory))
                    {
                        Directory.CreateDirectory(tempDirectory);
                        created = Directory.Exists(tempDirectory);
                    }
                }
                catch (IOException)
                {
                    if (tried > 8)
                    {
                        throw;
                    }
                }
            }
            if (!created)
            {
                throw new Exception("Could not create a temporary directory! Maybe all names are already used?");
            }
            return tempDirectory;
        }

        /// <summary>
        /// Creates the compile results for the specified <see cref="TypeContext"/>.
        /// </summary>
        /// <param name="context">The type context.</param>
        /// <returns>The compiler results.</returns>
        [Pure]
        [SecurityCritical]
        private Tuple<CompilerResults, string> Compile(TypeContext context)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            var compileUnit = GetCodeCompileUnit(context.ClassName, context.TemplateContent, context.Namespaces,
                                                 context.TemplateType, context.ModelType);

            var @params = new CompilerParameters
            {
                GenerateInMemory = false,
                GenerateExecutable = false,
                IncludeDebugInformation = Debug,
                TreatWarningsAsErrors = false,
                TempFiles = new TempFileCollection(GetTemporaryDirectory(), true),
                CompilerOptions = "/target:library /optimize /define:RAZORENGINE"
            };

            var assemblies = ReferenceResolver.GetReferences(context, IncludeAssemblies());

            assemblies = assemblies
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.InvariantCultureIgnoreCase);

            @params.ReferencedAssemblies.AddRange(assemblies.ToArray());
            var tempDir = @params.TempFiles.TempDir;
            var assemblyName = Path.Combine(tempDir,
                String.Format("{0}.{1}.dll", DynamicTemplateNamespace, context.ClassName));
            @params.TempFiles.AddFile(assemblyName, true);
            @params.OutputAssembly = assemblyName;
            
            string sourceCode = null;
            if (Debug)
            {
                var builder = new StringBuilder();
                using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
                {
                    _codeDomProvider.GenerateCodeFromCompileUnit(compileUnit, writer, new CodeGeneratorOptions());
                    sourceCode = builder.ToString();
                }
            }
            
            var results = _codeDomProvider.CompileAssemblyFromDom(@params, compileUnit);
            if (Debug)
            {
                bool written = false;
                var targetFile = Path.Combine(results.TempFiles.TempDir, "generated_template." + SourceFileExtension);
                if (!written && !File.Exists(targetFile))
                {
                    File.WriteAllText(targetFile, sourceCode);
                    written = true;
                }
                if (!written)
                {
                    foreach (string item in results.TempFiles)
	                {
                        if (item.EndsWith("." + this.SourceFileExtension))
                        {
                            File.Copy(item, targetFile, true);
                            written = true;
                            break;
                        }
	                } 
                }
            }
            return Tuple.Create(results, sourceCode);
        }

        /// <summary>
        /// Compiles the type defined in the specified type context.
        /// </summary>
        /// <param name="context">The type context which defines the type to compile.</param>
        /// <returns>The compiled type.</returns>
        [Pure, SecurityCritical]
        public override Tuple<Type, CompilationData> CompileType(TypeContext context)
        {
            if (context == null)
                throw new ArgumentNullException("context");

            (new PermissionSet(PermissionState.Unrestricted)).Assert();
            var result = Compile(context);
            var compileResult = result.Item1;

            CompilationData tmpDir;
            if (compileResult.TempFiles != null)
            {
                tmpDir = new CompilationData(result.Item2, compileResult.TempFiles.TempDir);
            }
            else
            {
                tmpDir = new CompilationData(result.Item2, null);
            }

            if (compileResult.Errors != null && compileResult.Errors.HasErrors)
            {
                throw new TemplateCompilationException(compileResult.Errors, tmpDir, context.TemplateContent);
            }
            // Make sure we load the assembly from a file and not with
            // Load(byte[]) (or it will be fully trusted!)
            var assemblyPath = compileResult.PathToAssembly;
            compileResult.CompiledAssembly = Assembly.LoadFile(assemblyPath);
            var type = compileResult.CompiledAssembly.GetType(DynamicTemplateNamespace + "." + context.ClassName);
            return Tuple.Create(type, tmpDir);
        }

        /// <summary>
        /// Releases managed resourced used by this instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases managed resources used by this instance.
        /// </summary>
        /// <param name="disposing">Are we explicily disposing of this instance?</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _codeDomProvider.Dispose();
                _disposed = true;
            }
        }
        #endregion
    }
}