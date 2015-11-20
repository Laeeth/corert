﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

using Internal.TypeSystem;

namespace Internal.TypeSystem.Ecma
{
    public sealed class EcmaField : FieldDesc, EcmaModule.IEntityHandleObject
    {
        private static class FieldFlags
        {
            public const int BasicMetadataCache     = 0x0001;
            public const int Static                 = 0x0002;
            public const int InitOnly               = 0x0004;
            public const int Literal                = 0x0008;
            public const int HasRva                 = 0x0010;

            public const int AttributeMetadataCache = 0x0100;
            public const int ThreadStatic           = 0x0200;
        };

        private EcmaType _type;
        private FieldDefinitionHandle _handle;

        // Cached values
        private ThreadSafeFlags _fieldFlags;
        private TypeDesc _fieldType;
        private string _name;

        internal EcmaField(EcmaType type, FieldDefinitionHandle handle)
        {
            _type = type;
            _handle = handle;

#if DEBUG
            // Initialize name eagerly in debug builds for convenience
            this.ToString();
#endif
        }

        EntityHandle EcmaModule.IEntityHandleObject.Handle
        {
            get
            {
                return _handle;
            }
        }


        public override TypeSystemContext Context
        {
            get
            {
                return _type.Module.Context;
            }
        }

        public override MetadataType OwningType
        {
            get
            {
                return _type;
            }
        }

        public EcmaModule Module
        {
            get
            {
                return _type.Module;
            }
        }

        public MetadataReader MetadataReader
        {
            get
            {
                return _type.MetadataReader;
            }
        }

        public FieldDefinitionHandle Handle
        {
            get
            {
                return _handle;
            }
        }

        private TypeDesc InitializeFieldType()
        {
            var metadataReader = MetadataReader;
            BlobReader signatureReader = metadataReader.GetBlobReader(metadataReader.GetFieldDefinition(_handle).Signature);

            EcmaSignatureParser parser = new EcmaSignatureParser(Module, signatureReader);
            var fieldType = parser.ParseFieldSignature();
            return (_fieldType = fieldType);
        }

        public override TypeDesc FieldType
        {
            get
            {
                if (_fieldType == null)
                    return InitializeFieldType();
                return _fieldType;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int InitializeFieldFlags(int mask)
        {
            int flags = 0;

            if ((mask & FieldFlags.BasicMetadataCache) != 0)
            {
                var fieldAttributes = Attributes;

                if ((fieldAttributes & FieldAttributes.Static) != 0)
                    flags |= FieldFlags.Static;

                if ((fieldAttributes & FieldAttributes.InitOnly) != 0)
                    flags |= FieldFlags.InitOnly;

                if ((fieldAttributes & FieldAttributes.Literal) != 0)
                    flags |= FieldFlags.Literal;

                if ((fieldAttributes & FieldAttributes.HasFieldRVA) != 0)
                    flags |= FieldFlags.HasRva;

                flags |= FieldFlags.BasicMetadataCache;
            }

            // Fetching custom attribute based properties is more expensive, so keep that under
            // a separate cache that might not be accessed very frequently.
            if ((mask & FieldFlags.AttributeMetadataCache) != 0)
            {
                var metadataReader = this.MetadataReader;
                var fieldDefinition = metadataReader.GetFieldDefinition(_handle);

                foreach (var attributeHandle in fieldDefinition.GetCustomAttributes())
                {
                    StringHandle namespaceHandle, nameHandle;
                    if (!metadataReader.GetAttributeNamespaceAndName(attributeHandle, out namespaceHandle, out nameHandle))
                        continue;

                    if (metadataReader.StringComparer.Equals(namespaceHandle, "System"))
                    {
                        if (metadataReader.StringComparer.Equals(nameHandle, "ThreadStaticAttribute"))
                        {
                            // TODO: Thread statics
                            //flags |= FieldFlags.ThreadStatic;
                        }
                    }
                }

                flags |= FieldFlags.AttributeMetadataCache;
            }

            Debug.Assert((flags & mask) != 0);

            _fieldFlags.AddFlags(flags);

            return flags & mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetFieldFlags(int mask)
        {
            int flags = _fieldFlags.Value & mask;
            if (flags != 0)
                return flags;
            return InitializeFieldFlags(mask);
        }

        public override bool IsStatic
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.Static) & FieldFlags.Static) != 0;
            }
        }

        public override bool IsThreadStatic
        {
            get
            {
                return IsStatic &&
                    (GetFieldFlags(FieldFlags.AttributeMetadataCache | FieldFlags.ThreadStatic) & FieldFlags.ThreadStatic) != 0;
            }
        }

        public override bool IsInitOnly
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.InitOnly) & FieldFlags.InitOnly) != 0;
            }
        }

        public override bool HasRva
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.HasRva) & FieldFlags.HasRva) != 0;
            }
        }

        public bool IsLiteral
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.Literal) & FieldFlags.Literal) != 0;
            }
        }

        public FieldAttributes Attributes
        {
            get
            {
                return MetadataReader.GetFieldDefinition(_handle).Attributes;
            }
        }

        private string InitializeName()
        {
            var metadataReader = MetadataReader;
            var name = metadataReader.GetString(metadataReader.GetFieldDefinition(_handle).Name);
            return (_name = name);
        }

        public override string Name
        {
            get
            {
                if (_name == null)
                    return InitializeName();
                return _name;
            }
        }

        public override bool HasCustomAttribute(string attributeNamespace, string attributeName)
        {
            return MetadataReader.HasCustomAttribute(MetadataReader.GetFieldDefinition(_handle).GetCustomAttributes(),
                attributeNamespace, attributeName);
        }

        public override string ToString()
        {
            return _type.ToString() + "." + Name;
        }
    }

    public static class EcmaFieldExtensions
    {
        /// <summary>
        /// Retrieves the data associated with an RVA mapped field from the PE module.
        /// </summary>
        public static byte[] GetFieldRvaData(this EcmaField field)
        {
            Debug.Assert(field.HasRva);
            int addr = field.MetadataReader.GetFieldDefinition(field.Handle).GetRelativeVirtualAddress();
            var memBlock = field.Module.PEReader.GetSectionData(addr).GetContent();

            var fieldType = (EcmaType)field.FieldType;
            int size = fieldType.MetadataReader.GetTypeDefinition(fieldType.Handle).GetLayout().Size;
            if (size == 0)
                throw new NotImplementedException();

            byte[] result = new byte[size];
            memBlock.CopyTo(0, result, 0, result.Length);

            return result;
        }
    }
}
