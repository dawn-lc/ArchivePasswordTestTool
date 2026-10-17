using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    internal enum AskMode : int
    {
        Extract = 0,
        Test,
        Skip
    }

    internal enum OperationResult : int
    {
        OK = 0,
        UnsupportedMethod,
        DataError,
        CRCError
    }

    internal enum ItemPropId : uint
    {
        NoProperty = 0,
        HandlerItemIndex = 2,
        Path,
        Name,
        Extension,
        IsFolder,
        Size,
        PackedSize,
        Attributes,
        CreationTime,
        LastAccessTime,
        LastWriteTime,
        Solid,
        Commented,
        Encrypted,
        SplitBefore,
        SplitAfter,
        DictionarySize,
        CRC,
        Type,
        IsAnti,
        Method,
        HostOS,
        FileSystem,
        User,
        Group,
        Block,
        Comment,
        Position,
        Prefix,
        TotalSize = 0x1100,
        FreeSpace,
        ClusterSize,
        VolumeName,
        LocalName = 0x1200,
        Provider,
        UserDefined = 0x10000
    }

    internal enum ArchivePropId : uint
    {
        Name = 0,
        ClassID,
        Extension,
        AddExtension,
        Update,
        KeepName,
        StartSignature,
        FinishSignature,
        Associate
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly DateTime ToDateTime()
        {
            long filetime = ((long)dwHighDateTime << 32) | dwLowDateTime;
            return DateTime.FromFileTimeUtc(filetime);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropArray
    {
        public uint Length;
        public IntPtr PointerValues;

        public readonly IntPtr GetElementPtr(int index)
        {
            int size = IntPtr.Size;
            return IntPtr.Add(PointerValues, index * size);
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;

        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public byte byteValue;
        [FieldOffset(8)] public short boolValue;
        [FieldOffset(8)] public long longValue;
        [FieldOffset(8)] public FILETIME filetime;
        [FieldOffset(8)] public PropArray propArray;

        public readonly VarEnum VarType => (VarEnum)vt;

        public bool Bool
        {
            get
            {
                if (VarType != VarEnum.VT_BOOL)
                    return false;

                return boolValue != 0;
            }
        }

        public ulong UInt64
        {
            get
            {
                return VarType switch
                {
                    VarEnum.VT_UI8 => (ulong)longValue,
                    VarEnum.VT_UI4 => (uint)longValue,
                    VarEnum.VT_UI2 => (ushort)longValue,
                    VarEnum.VT_UI1 => byteValue,
                    _ => 0
                };
            }
        }

        public long Int64
        {
            get
            {
                return VarType switch
                {
                    VarEnum.VT_I8 => longValue,
                    VarEnum.VT_I4 => (int)longValue,
                    VarEnum.VT_I2 => (short)longValue,
                    VarEnum.VT_I1 => (sbyte)byteValue,
                    _ => 0
                };
            }
        }

        public string? String
        {
            get
            {
                if (VarType == VarEnum.VT_BSTR && pointerValue != IntPtr.Zero)
                    return Marshal.PtrToStringBSTR(pointerValue);

                if (VarType == VarEnum.VT_LPWSTR && pointerValue != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pointerValue);

                return null;
            }
        }

        public DateTime? DateTime
        {
            get
            {
                if (VarType == VarEnum.VT_FILETIME)
                    return filetime.ToDateTime();

                return null;
            }
        }

        public object? GetObject()
        {
            return VarType switch
            {
                VarEnum.VT_EMPTY => null,
                VarEnum.VT_BOOL => Bool,
                VarEnum.VT_UI1 => byteValue,
                VarEnum.VT_UI2 => (ushort)longValue,
                VarEnum.VT_UI4 => (uint)longValue,
                VarEnum.VT_UI8 => (ulong)longValue,
                VarEnum.VT_I8 => longValue,
                VarEnum.VT_BSTR => String,
                VarEnum.VT_LPWSTR => String,
                VarEnum.VT_FILETIME => DateTime,
                _ => MarshalVariant()
            };
        }

        public void Clear()
        {
            switch (VarType)
            {
                case VarEnum.VT_EMPTY:
                case VarEnum.VT_NULL:
                case VarEnum.VT_I2:
                case VarEnum.VT_I4:
                case VarEnum.VT_R4:
                case VarEnum.VT_R8:
                case VarEnum.VT_CY:
                case VarEnum.VT_DATE:
                case VarEnum.VT_ERROR:
                case VarEnum.VT_BOOL:
                case VarEnum.VT_I1:
                case VarEnum.VT_UI1:
                case VarEnum.VT_UI2:
                case VarEnum.VT_UI4:
                case VarEnum.VT_I8:
                case VarEnum.VT_UI8:
                case VarEnum.VT_INT:
                case VarEnum.VT_UINT:
                case VarEnum.VT_HRESULT:
                case VarEnum.VT_FILETIME:

                    vt = 0;
                    pointerValue = IntPtr.Zero;
                    break;

                default:
                    int hr = WindowsNativeMethods.PropVariantClear(ref this);

                    if (hr != 0)
                        Marshal.ThrowExceptionForHR(hr);

                    break;
            }
        }

        private readonly object? MarshalVariant()
        {
            GCHandle handle = GCHandle.Alloc(this, GCHandleType.Pinned);

            try
            {
                return Marshal.GetObjectForNativeVariant(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }


    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600600000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInArchive
    {
        [PreserveSig]
        int Open(
            IInStream stream,
            [In] ref ulong maxCheckStartPosition,
            [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback? openCallback
        );

        // native: Close()
        [PreserveSig]
        int Close();

        // native: GetNumberOfItems(UInt32 *numItems)
        [PreserveSig]
        int GetNumberOfItems(out uint count);

        // native: GetProperty(UInt32 index, PROPID propID, PROPVARIANT *value)
        [PreserveSig]
        int GetProperty(uint index, ItemPropId propID, ref PropVariant value);

        [PreserveSig]
        int Extract(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? indices,
            uint numItems,
            int testMode,
            [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);

        // native: GetArchiveProperty(PROPID propID, PROPVARIANT *value)
        [PreserveSig]
        int GetArchiveProperty(uint propID, ref PropVariant value);

        // native: GetNumberOfProperties(UInt32 *numProps)
        [PreserveSig]
        int GetNumberOfProperties(out uint numProps);

        // native: GetPropertyInfo(UInt32 index, BSTR *name, PROPID *propID, VARTYPE *varType)
        [PreserveSig]
        int GetPropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] out string name,
            out ItemPropId propID,
            out ushort varType);

        // native: GetNumberOfArchiveProperties(UInt32 *numProps)
        [PreserveSig]
        int GetNumberOfArchiveProperties(out uint numProps);

        // native: GetArchivePropertyInfo(UInt32 index, BSTR *name, PROPID *propID, VARTYPE *varType)
        [PreserveSig]
        int GetArchivePropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] out string name,
            out uint propID,
            out ushort varType);
    }

    // 官方 GUID: IStream.h 中 IInStream = 03 组 0x03
    // 注意：IInStream 继承 ISequentialInStream，其 Read 是 3 参数签名（含 processedSize 出参）
    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000300030000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInStream
    {
        // ISequentialInStream::Read(void *data, UInt32 size, UInt32 *processedSize)
        // 调用方承诺 processedSize != NULL，实现必须写入实际读取字节数，返回 HRESULT
        [PreserveSig]
        int Read(
            IntPtr data,
            uint size,
            IntPtr processedSize);

        // IInStream::Seek(Int64 offset, UInt32 seekOrigin, UInt64 *newPosition)
        [PreserveSig]
        int Seek(
            long offset,
            uint seekOrigin,
            IntPtr newPosition);
    }

    // 官方 GUID: {23170F69-40C1-278A-0000-00yy00xx0000}, IArchive.h 中 IArchiveOpenCallback = 06 组 0x10
    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenCallback
    {
        // native: SetTotal(const UInt64 *files, const UInt64 *bytes)
        [PreserveSig]
        int SetTotal(IntPtr files, IntPtr bytes);

        // native: SetCompleted(const UInt64 *files, const UInt64 *bytes)
        [PreserveSig]
        int SetCompleted(IntPtr files, IntPtr bytes);
    }

    // 官方 GUID: Guid.txt 06 组 0x30 —— 打开分卷压缩包（secret.7z.001/.002...）时，
    // 7-Zip 会 QI 此接口并向回调索要后续分卷的流；不实现则 QI 失败，只能解鬳第一个分卷。
    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600300000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenVolumeCallback
    {
        // native: GetProperty(PROPID propID, PROPVARIANT *value)
        [PreserveSig]
        int GetProperty(uint propID, ref PropVariant value);

        // native: GetStream(const wchar_t *name, IInStream **inStream)
        [PreserveSig]
        int GetStream(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            out IInStream? inStream);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600200000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveExtractCallback
    {
        // native: SetTotal(UInt64 total)
        [PreserveSig]
        int SetTotal(ulong total);

        // native: SetCompleted(const UInt64 *completeValue)
        [PreserveSig]
        int SetCompleted([In] ref ulong completeValue);

        // native: GetStream(UInt32 index, ISequentialOutStream **outStream, Int32 askExtractMode)
        [PreserveSig]
        int GetStream(
            uint index,
            [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? outStream,
            AskMode askExtractMode);

        // native: PrepareOperation(Int32 askExtractMode)
        [PreserveSig]
        int PrepareOperation(AskMode askExtractMode);

        // native: SetOperationResult(Int32 opRes)
        [PreserveSig]
        int SetOperationResult(OperationResult resultEOperationResult);
    }

    // 官方 GUID: IStream.h 中 ISequentialOutStream = 03 组 0x02
    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000300020000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialOutStream
    {
        // ISequentialOutStream::Write(const void *data, UInt32 size, UInt32 *processedSize)
        // 注意：data 用 IntPtr 而非 byte[]——若用 byte[] 必须加 [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        // 否则 CCW stub 无法确定数组长度，native 传入真实数据时会越界 marshal 导致崩溃。
        // 测试模式无需访问数据内容，只需要回写 processedSize，用 IntPtr 最安全高效。
        [PreserveSig]
        int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000500100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICryptoGetTextPassword
    {
        [PreserveSig]
        int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password);
    }
}
