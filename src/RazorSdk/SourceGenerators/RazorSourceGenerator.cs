// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    [Generator]
    public partial class RazorSourceGenerator : ISourceGenerator
    {
        private static Dictionary<MetadataId, IReadOnlyList<TagHelperDescriptor>> _tagHelperCache = new Dictionary<MetadataId, IReadOnlyList<TagHelperDescriptor>>();

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var razorContext = RazorSourceGenerationContext.Create(context);
            if (razorContext is null ||
                (razorContext.RazorFiles.Count == 0 && razorContext.CshtmlFiles.Count == 0))
            {
                return;
            }

            HandleDebugSwitch(razorContext.WaitForDebugger);

            var tagHelpers = ResolveTagHelperDescriptors(context, razorContext);

            var projectEngine = RazorProjectEngine.Create(razorContext.Configuration, razorContext.FileSystem, b =>
            {
                b.Features.Add(new DefaultTypeNameFeature());
                b.SetRootNamespace(razorContext.RootNamespace);

                b.Features.Add(new StaticTagHelperFeature { TagHelpers = tagHelpers, });
                b.Features.Add(new DefaultTagHelperDescriptorProvider());

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(((CSharpParseOptions)context.ParseOptions).LanguageVersion);
            });

            CodeGenerateRazorComponents(context, razorContext, projectEngine);
            GenerateViews(context, razorContext, projectEngine);
        }

        private void GenerateViews(GeneratorExecutionContext context, RazorSourceGenerationContext razorContext, RazorProjectEngine projectEngine)
        {
            var files = razorContext.CshtmlFiles;

            Parallel.For(0, files.Count, GetParallelOptions(context), i =>
            {
                var file = files[i];

                var codeDocument = projectEngine.Process(projectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Legacy));
                var csharpDocument = codeDocument.GetCSharpDocument();
                for (var j = 0; j < csharpDocument.Diagnostics.Count; j++)
                {
                    var razorDiagnostic = csharpDocument.Diagnostics[j];
                    var csharpDiagnostic = razorDiagnostic.AsDiagnostic();
                    context.ReportDiagnostic(csharpDiagnostic);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.GeneratedOutputPath));
                File.WriteAllText(file.GeneratedOutputPath, csharpDocument.GeneratedCode);
            });
        }

        private static void CodeGenerateRazorComponents(GeneratorExecutionContext context, RazorSourceGenerationContext razorContext, RazorProjectEngine projectEngine)
        {
            var files = razorContext.RazorFiles;

            var arraypool = ArrayPool<(string, SourceText)>.Shared;
            var outputs = arraypool.Rent(files.Count);

            Parallel.For(0, files.Count, GetParallelOptions(context), i =>
            {
                var file = files[i];
                var projectItem = projectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Component);

                var codeDocument = projectEngine.Process(projectItem);
                var csharpDocument = codeDocument.GetCSharpDocument();
                for (var j = 0; j < csharpDocument.Diagnostics.Count; j++)
                {
                    var razorDiagnostic = csharpDocument.Diagnostics[j];
                    var csharpDiagnostic = razorDiagnostic.AsDiagnostic();
                    context.ReportDiagnostic(csharpDiagnostic);
                }

                var hint = GetIdentifierFromPath(file.NormalizedPath);

                var generatedCode = csharpDocument.GeneratedCode;
                if (razorContext.WriteGeneratedContent)
                {
                    var path = file.GeneratedOutputPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, generatedCode);
                }

                outputs[i] = (hint, SourceText.From(generatedCode, Encoding.UTF8));
            });

            for (var i = 0; i < files.Count; i++)
            {
                var (hint, sourceText) = outputs[i];
                context.AddSource(hint, sourceText);
            }

            arraypool.Return(outputs);
        }

        private static IReadOnlyList<TagHelperDescriptor> ResolveTagHelperDescriptors(GeneratorExecutionContext GeneratorExecutionContext, RazorSourceGenerationContext razorContext)
        {
            var tagHelperFeature = new StaticCompilationTagHelperFeature();

            var langVersion = ((CSharpParseOptions)GeneratorExecutionContext.ParseOptions).LanguageVersion;

            var discoveryProjectEngine = RazorProjectEngine.Create(razorContext.Configuration, razorContext.FileSystem, b =>
            {
                b.Features.Add(new DefaultTypeNameFeature());
                b.Features.Add(new ConfigureRazorCodeGenerationOptions(options =>
                {
                    options.SuppressPrimaryMethodBody = true;
                    options.SuppressChecksum = true;
                }));

                b.SetRootNamespace(razorContext.RootNamespace);

                var metadataReferences = new List<MetadataReference>(GeneratorExecutionContext.Compilation.References);
                b.Features.Add(new DefaultMetadataReferenceFeature { References = metadataReferences });

                b.Features.Add(tagHelperFeature);
                b.Features.Add(new DefaultTagHelperDescriptorProvider());

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(langVersion);
            });

            var files = razorContext.RazorFiles;
            var results = ArrayPool<SyntaxTree>.Shared.Rent(files.Count);

            var parseOptions = (CSharpParseOptions)GeneratorExecutionContext.ParseOptions;

            Parallel.For(0, files.Count, GetParallelOptions(GeneratorExecutionContext), i =>
            {
                var file = files[i];
                if (File.GetLastWriteTimeUtc(file.GeneratedDeclarationPath) > File.GetLastWriteTimeUtc(file.AdditionalText.Path))
                {
                    // Declaration files are invariant to other razor files, tag helpers, assemblies. If we have previously generated
                    // content that it's still newer than the output file, use it and save time processing the file.
                    using var outputFileStream = File.OpenRead(file.GeneratedDeclarationPath);
                    results[i] = CSharpSyntaxTree.ParseText(
                        SourceText.From(outputFileStream),
                        options: parseOptions);
                }
                else
                {
                    var codeGen = discoveryProjectEngine.Process(discoveryProjectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Component));
                    var generatedCode = codeGen.GetCSharpDocument().GeneratedCode;

                    Directory.CreateDirectory(Path.GetDirectoryName(file.GeneratedDeclarationPath));
                    File.WriteAllText(file.GeneratedDeclarationPath, generatedCode);

                    results[i] = CSharpSyntaxTree.ParseText(
                        generatedCode,
                        options: parseOptions);
                }
            });

            tagHelperFeature.Compilation = GeneratorExecutionContext.Compilation.AddSyntaxTrees(results.Take(files.Count));
            ArrayPool<SyntaxTree>.Shared.Return(results);

            return GetTagHelperDescriptorsFromReferences(GeneratorExecutionContext.Compilation, tagHelperFeature);
        }

        private static IReadOnlyList<TagHelperDescriptor> GetTagHelperDescriptorsFromReferences(Compilation compilation, StaticCompilationTagHelperFeature tagHelperFeature)
        {
            List<TagHelperDescriptor> tagHelperDescriptors = new();

            foreach (var reference in compilation.References)
            {
                var guid = ((PortableExecutableReference) reference).GetMetadataId();
                if (!_tagHelperCache.TryGetValue(guid, out var descriptors))
                {
                    tagHelperFeature.TargetReference = reference;
                    descriptors = tagHelperFeature.GetDescriptors();
                    _tagHelperCache.Add(guid, descriptors);
                }

                tagHelperDescriptors.AddRange(descriptors);
            }

            return tagHelperDescriptors;
        }

        private static string GetIdentifierFromPath(string filePath)
        {
            var builder = new StringBuilder(filePath.Length);

            for (var i = 0; i < filePath.Length; i++)
            {
                builder.Append(filePath[i] switch
                {
                    ':' or '\\' or '/' => '_',
                    var @default => @default,
                });
            }

            return builder.ToString();
        }

        private static ParallelOptions GetParallelOptions(GeneratorExecutionContext generatorExecutionContext)
        {
            var options = new ParallelOptions { CancellationToken = generatorExecutionContext.CancellationToken };
            var isConcurrentBuild = generatorExecutionContext.Compilation.Options.ConcurrentBuild;
            if (Debugger.IsAttached || !isConcurrentBuild)
            {
                options.MaxDegreeOfParallelism = 1;
            }
            return options;
        }

        private static void HandleDebugSwitch(bool waitForDebugger)
        {
            if (waitForDebugger)
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
