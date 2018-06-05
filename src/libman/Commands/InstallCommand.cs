﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Web.LibraryManager.Contracts;

namespace Microsoft.Web.LibraryManager.Tools.Commands
{
    /// <summary>
    /// Defines the libman install command to allow users to install libraries.
    /// </summary>
    /// <remarks>Creates a new libman.json if one doesn't exist in current directory.</remarks>
    internal class InstallCommand : BaseCommand
    {
        public InstallCommand(IHostEnvironment hostEnvironment, bool throwOnUnexpectedArg = true)
            : base(throwOnUnexpectedArg, "install", Resources.InstallCommandDesc, hostEnvironment)
        {
        }

        /// <summary>
        /// Argument to specify the library to install.
        /// </summary>
        /// <remarks>LibraryId is required argument.</remarks>
        public CommandArgument LibraryId { get; private set; }

        /// <summary>
        /// Option to specify the provider to use for installing the library.
        /// </summary>
        public CommandOption Provider { get; private set; }

        /// <summary>
        /// Option to specify the destination for the library.
        /// </summary>
        public CommandOption Destination { get; set; }

        /// <summary>
        /// Option to specify files for the library.
        /// </summary>
        /// <remarks>Allows specifying multiple values</remarks>
        public CommandOption Files { get; set; }


        private Manifest _manifest;
        private IProvider _provider;
        private ILibraryCatalog _catalog;

        private string ProviderId => Provider.HasValue() ? Provider.Value() : _manifest.DefaultProvider;

        private IProvider ProviderToUse
        {
            get
            {
                if (_provider == null)
                {
                    _provider = ManifestDependencies.GetProvider(ProviderId);
                }

                return _provider;
            }
        }

        private ILibraryCatalog ProviderCatalog
        {
            get
            {
                if (_catalog == null)
                {
                    _catalog = ProviderToUse.GetCatalog();
                }

                return _catalog;
            }
        }

        public override BaseCommand Configure(CommandLineApplication parent)
        {
            base.Configure(parent);

            LibraryId = Argument("libraryId", Resources.InstallCommandLibraryIdArgumentDesc, multipleValues: false);
            Provider = Option("--provider|-p", Resources.ProviderOptionDesc, CommandOptionType.SingleValue);
            Destination = Option("--destination|-d", Resources.DestinationOptionDesc, CommandOptionType.SingleValue);
            Files = Option("--files", Resources.FilesOptionDesc, CommandOptionType.MultipleValue);

            return this;
        }

        protected override async Task<int> ExecuteInternalAsync()
        {
            _manifest = await GetManifestAsync(createIfNotExists: true);

            ValidateParameters(_manifest);

            List<string> files = Files.HasValue() ? Files.Values : null;

            (string libraryId, ILibrary library) = await ValidateLibraryExistsInCatalogAsync(CancellationToken.None);

            IEnumerable<ILibraryOperationResult> results = await _manifest.InstallLibraryAsync(libraryId, Provider.Value(), files, Destination.Value(), CancellationToken.None);

            if (results.All(r => r.Success))
            {
                await _manifest.SaveAsync(Settings.ManifestFileName, CancellationToken.None);
                string installDestination = Destination.HasValue() ? Destination.Value() : _manifest.DefaultDestination;
                Logger.Log(string.Format(Resources.InstalledLibrary, libraryId, installDestination), LogLevel.Operation);
            }
            else
            {
                bool isFileConflicts = false;
                Logger.Log(string.Format(Resources.InstallLibraryFailed, libraryId), LogLevel.Error);
                foreach (ILibraryOperationResult result in results)
                {
                    foreach (IError error in result.Errors)
                    {
                        Logger.Log(string.Format("[{0}]: {1}", error.Code, error.Message), LogLevel.Error);
                        if(error.Code == PredefinedErrors.ConflictingLibrariesInManifest("", new List<string>()).Code)
                        {
                            isFileConflicts = true;
                        }
                    }
                }

                if (isFileConflicts)
                {
                    Logger.Log(Resources.SpecifyDifferentDestination, LogLevel.Error);
                }
            }

            return 0;
        }

        private async Task<(string libraryId, ILibrary library)> ValidateLibraryExistsInCatalogAsync(CancellationToken cancellationToken)
        {
            try
            {
                ILibrary libraryToInstall = await ProviderCatalog.GetLibraryAsync(LibraryId.Value, cancellationToken);

                if (libraryToInstall != null)
                {
                    return (LibraryId.Value, libraryToInstall);
                }
            }
            catch
            {
                // The library id wasn't in the exact format.
            }

            IReadOnlyList<ILibraryGroup> libraryGroup = await ProviderCatalog.SearchAsync(LibraryId.Value, 5, cancellationToken);

            IError invalidLibraryError = PredefinedErrors.UnableToResolveSource(LibraryId.Value, ProviderId);
            if (libraryGroup.Count == 0)
            {
                throw new InvalidOperationException($"[{invalidLibraryError.Code}]: {invalidLibraryError.Message}");
            }

            var sb = new StringBuilder();

            foreach (ILibraryGroup libGroup in libraryGroup)
            {
                IEnumerable<string> libIds = await libGroup.GetLibraryIdsAsync(cancellationToken);
                if (libIds == null || !libIds.Any())
                {
                    continue;
                }

                if (libGroup.DisplayName.Equals(LibraryId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    // Found a group with an exact match.
                    string libraryId = libIds.First();
                    ILibrary libraryToInstall = await ProviderCatalog.GetLibraryAsync(libraryId, cancellationToken);

                    return (libraryId, libraryToInstall);
                }

                sb.AppendLine("  " + libIds.First());
            }

            sb.Insert(0, $"[{invalidLibraryError.Code}]: {invalidLibraryError.Message} {Environment.NewLine} {Resources.SuggestedIdsMessage}{Environment.NewLine}");
            throw new InvalidOperationException(sb.ToString());
        }

        private void ValidateParameters(Manifest manifest)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(LibraryId.Value))
            {
                errors.Add(Resources.LibraryIdRequiredForInstall);
            }

            if (string.IsNullOrWhiteSpace(manifest.DefaultDestination) && !Destination.HasValue())
            {
                errors.Add(Resources.DestinationRequiredWhenNoDefaultIsPresent);
            }

            if (string.IsNullOrWhiteSpace(manifest.DefaultProvider) && !Provider.HasValue())
            {
                errors.Add(Resources.ProviderRequiredWhenNoDefaultIsPresent);
            }

            string providerId = Provider.HasValue() ? Provider.Value() : manifest.DefaultProvider;

            if (!ManifestDependencies.Providers.Any(p => p.Id == providerId))
            {
                errors.Add(string.Format(Resources.ProviderNotInstalled, providerId));
            }

            if (errors.Any())
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            }
        }

        public override string Examples => Resources.InstallCommandExamples;
        public override string Remarks => Resources.InstallCommandRemarks;

    }
}