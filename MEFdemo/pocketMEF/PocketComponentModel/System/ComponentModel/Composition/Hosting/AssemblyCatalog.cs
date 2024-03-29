﻿// -----------------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Primitives;
using System.ComponentModel.Composition.ReflectionModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using Microsoft.Internal;

namespace System.ComponentModel.Composition.Hosting
{
    /// <summary>
    ///     An immutable ComposablePartCatalog created from a managed code assembly.
    /// </summary>
    /// <remarks>
    ///     This type is thread safe.
    /// </remarks>
    //[DebuggerTypeProxy(typeof(AssemblyCatalogDebuggerProxy))]
    public class AssemblyCatalog : ComposablePartCatalog, ICompositionElement
    {
        private readonly object _thisLock = new object();
        private readonly ICompositionElement _definitionOrigin;
        private volatile Assembly _assembly = null;
        private volatile TypeCatalog _innerCatalog = null;
        private int _isDisposed = 0;

#if !SILVERLIGHT

        /// <summary>
        ///     Initializes a new instance of the <see cref="AssemblyCatalog"/> class 
        ///     with the specified code base.
        /// </summary>
        /// <param name="codeBase">
        ///     A <see cref="String"/> containing the code base of the assembly containing the
        ///     attributed <see cref="Type"/> objects to add to the <see cref="AssemblyCatalog"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="codeBase"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///     <paramref name="codeBase"/> is a zero-length string, contains only white space, 
        ///     or contains one or more invalid characters as defined by <see cref="Path.InvalidPathChars"/>.
        /// </exception>
        /// <exception cref="PathTooLongException">
        ///     The specified path, file name, or both exceed the system-defined maximum length. 
        /// </exception>
        /// <exception cref="SecurityException">
        ///     The caller does not have path discovery permission. 
        /// </exception>
        /// <exception cref="FileNotFoundException">
        ///     <paramref name="codeBase"/> is not found.
        /// </exception>
        /// <exception cref="FileLoadException ">
        ///     <paramref name="codeBase"/> could not be loaded.
        ///     <para>
        ///         -or-
        ///     </para>
        ///     <paramref name="codeBase"/> specified a directory.
        /// </exception>
        /// <exception cref="BadImageFormatException">
        ///     <paramref name="codeBase"/> is not a valid assembly
        ///     -or- 
        ///     Version 2.0 or later of the common language runtime is currently loaded 
        ///     and <paramref name="codeBase"/> was compiled with a later version. 
        /// </exception>
        /// <remarks>
        ///     The assembly referenced by <paramref langword="codeBase"/> is loaded into the Load context.
        /// </remarks>
        public AssemblyCatalog(string codeBase)
            : this(codeBase, (ICompositionElement)null)
        {
        }

        internal AssemblyCatalog(string codeBase, ICompositionElement definitionOrigin)
            : this(LoadAssembly(codeBase), definitionOrigin)
        {
        }

#endif

        /// <summary>
        ///     Initializes a new instance of the <see cref="AssemblyCatalog"/> class 
        ///     with the specified assembly.
        /// </summary>
        /// <param name="assembly">
        ///     The <see cref="Assembly"/> containing the attributed <see cref="Type"/> objects to 
        ///     add to the <see cref="AssemblyCatalog"/>.
        /// </param>
        /// <exception cref="ArgumentException">
        ///     <paramref name="assembly"/> is <see langword="null"/>.
        ///     <para>
        ///         -or-
        ///     </para>    
        ///     <paramref name="assembly"/> was loaded in the reflection-only context.
        /// </exception>
        public AssemblyCatalog(Assembly assembly)
            : this(assembly, (ICompositionElement)null)
        {
        }

        internal AssemblyCatalog(Assembly assembly, ICompositionElement definitionOrigin)
        {
            Requires.NotNull(assembly, "assembly");

//#if !SILVERLIGHT
//            if (assembly.ReflectionOnly)
//            {
//                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Argument_AssemblyReflectionOnly, "assembly"), "assembly");
//            }
//#endif
            this._assembly = assembly;
            this._definitionOrigin = definitionOrigin ?? this;
        }

        /// <summary>
        ///     Gets the part definitions of the assembly catalog.
        /// </summary>
        /// <value>
        ///     A <see cref="IEnumerable{T}"/> of <see cref="ComposablePartDefinition"/> objects of the 
        ///     <see cref="AssemblyCatalog"/>.
        /// </value>
        /// <exception cref="ObjectDisposedException">
        ///     The <see cref="AssemblyCatalog"/> has been disposed of.
        /// </exception>
        public override IEnumerable<ComposablePartDefinition> Parts
        {
            get
            {
                return this.InnerCatalog.Parts;
            }
        }

        /// <summary>
        ///     Returns the export definitions that match the constraint defined by the specified definition.
        /// </summary>
        /// <param name="definition">
        ///     The <see cref="ImportDefinition"/> that defines the conditions of the 
        ///     <see cref="ExportDefinition"/> objects to return.
        /// </param>
        /// <returns>
        ///     An <see cref="IEnumerable{T}"/> of <see cref="Tuple{T1, T2}"/> containing the 
        ///     <see cref="ExportDefinition"/> objects and their associated 
        ///     <see cref="ComposablePartDefinition"/> for objects that match the constraint defined 
        ///     by <paramref name="definition"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        ///     <paramref name="definition"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        ///     The <see cref="ComposablePartCatalog"/> has been disposed of.
        /// </exception>
        /// <remarks>
        ///     <note type="inheritinfo">
        ///         Overriders of this property should never return <see langword="null"/>, if no 
        ///         <see cref="ExportDefinition"/> match the conditions defined by 
        ///         <paramref name="definition"/>, return an empty <see cref="IEnumerable{T}"/>.
        ///     </note>
        /// </remarks>
        public override IEnumerable<Tuple<ComposablePartDefinition, ExportDefinition>> GetExports(ImportDefinition definition)
        {
            return this.InnerCatalog.GetExports(definition);
        }

        private TypeCatalog InnerCatalog
        {
            get
            {
                this.ThrowIfDisposed();

                if (this._innerCatalog == null)
                {
                    lock (this._thisLock)
                    {
                        if (this._innerCatalog == null)
                        {
                            var catalog = new TypeCatalog(this._assembly.GetTypes(), _definitionOrigin);
                            this._innerCatalog = catalog;
                        }
                    }
                }
                return this._innerCatalog;
            }
        }

        /// <summary>
        ///     Gets the assembly containing the attributed types contained within the assembly
        ///     catalog.
        /// </summary>
        /// <value>
        ///     The <see cref="Assembly"/> containing the attributed <see cref="Type"/> objects
        ///     contained within the <see cref="AssemblyCatalog"/>.
        /// </value>
        public Assembly Assembly
        {
            get { return this._assembly; }
        }

        /// <summary>
        ///     Gets the display name of the assembly catalog.
        /// </summary>
        /// <value>
        ///     A <see cref="String"/> containing a human-readable display name of the <see cref="AssemblyCatalog"/>.
        /// </value>
        string ICompositionElement.DisplayName
        {
            get { return this.GetDisplayName(); }
        }

        /// <summary>
        ///     Gets the composition element from which the assembly catalog originated.
        /// </summary>
        /// <value>
        ///     This property always returns <see langword="null"/>.
        /// </value>
        ICompositionElement ICompositionElement.Origin
        {
            get { return null; }
        }


        /// <summary>
        ///     Returns a string representation of the assembly catalog.
        /// </summary>
        /// <returns>
        ///     A <see cref="String"/> containing the string representation of the <see cref="AssemblyCatalog"/>.
        /// </returns>
        public override string ToString()
        {
            return this.GetDisplayName();
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) == 0)
            {
                try
                {
                    if (disposing)
                    {
                        if (this._innerCatalog != null)
                        {
                            this._innerCatalog.Dispose();
                        }
                    }
                }
                finally
                {
                    base.Dispose(disposing);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (this._isDisposed == 1)
            {
                throw ExceptionBuilder.CreateObjectDisposed(this);
            }
        }

        private string GetDisplayName()
        {
            return string.Format(CultureInfo.CurrentCulture,
                                "{0} (Assembly=\"{1}\")",   // NOLOC
                                GetType().Name, 
                                this.Assembly.FullName);
        }

#if !SILVERLIGHT

        private static Assembly LoadAssembly(string codeBase)
        {
            Requires.NotNullOrEmpty(codeBase, "codeBase");

            try
            {
                return Assembly.LoadFrom(codeBase);
            }
            catch (ArgumentException)
            {
                AssemblyName assemblyName = new AssemblyName();
                assemblyName.CodeBase = codeBase;
                return Assembly.Load(assemblyName);
            }

            //AssemblyName assemblyName;
            //try
            //{
            //    assemblyName = AssemblyName.GetAssemblyName(codeBase);
            //}
            //catch (ArgumentException)
            //{
            //    assemblyName = new AssemblyName();
            //    assemblyName.CodeBase = codeBase;
            //}

            //return Assembly.Load(assemblyName);            
        }
#endif
    }
}
