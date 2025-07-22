//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Exceptions;
using Endianess = ELFSharp.ELF.Endianess;

namespace Antmicro.Renode.Peripherals.Memory
{
    public class FMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IMemory, IMultibyteWritePeripheral, IQuadWordPeripheral, ICanLoadFiles, IEndiannessAware
    {
        private Random rnd = new Random();
        static string pattern = "^([0-9]+):(zero|random|swap)";
        Regex rg = new Regex(pattern);
        public FMemory(byte[] source)
        {
            array = source;
            devid = 0;
        }
        public FMemory(ulong size,uint id)
        {
            if(size > MaxSize)
            {
                throw new ConstructionException($"Memory size cannot be larger than 0x{MaxSize:X}, requested: 0x{size:X}");
            }
            array = new byte[size];
            devid = id;
        }

        public virtual ulong ReadQuadWord(long offset)
        {
            if(!IsCorrectOffset(offset, sizeof(ulong)))
            {
                return 0;
            }
            var intOffset = (int)offset;
            var result = BitConverter.ToUInt64(array, intOffset);
            return result;
        }

        public virtual void WriteQuadWord(long offset, ulong value)
        {
            if(!IsCorrectOffset(offset, sizeof(ulong)))
            {
                return;
            }
            var bytes = BitConverter.GetBytes(value);
            bytes.CopyTo(array, offset);
        }

        public uint ReadDoubleWord(long offset)
        {
            if(!IsCorrectOffset(offset, sizeof(uint)))
            {
                return 0;
            }
            var intOffset = (int)offset;
            var result = BitConverter.ToUInt32(array, intOffset);
            return result;
        }

        public virtual void WriteDoubleWord(long offset, uint value)
        {
            if(!IsCorrectOffset(offset, sizeof(uint)))
            {
                return;
            }
            var bytes = BitConverter.GetBytes(value);
            bytes.CopyTo(array, offset);
        }

        public void Reset()
        {
            // nothing happens
        }

        public void DoTheThing()
        {
            
            string before = Convert.ToBase64String(array);
            int resetMode = 0;
            foreach (string line in File.ReadLines(@"/home/cliff/renode.config"))
            {
                if (rg.IsMatch(line))
                {
                  var match = Regex.Match(line, pattern);
                  

                  if (match.Groups[1].Value == devid.ToString())
                  {
                    this.Log(LogLevel.Debug, "Will {0}",match.Groups[2].Value);
                    if (line.Contains("zero")) // zero it out
                    {
                        resetMode = 0;
                        break;
                    }
                    else if (line.Contains("random")) //random change
                    {
                        resetMode = 1;
                        break;
                    }
                    else if (line.Contains("swap")){
                        resetMode = 2; // randomize the register value
                        break;
                    }
                    }
                }
            }
            for (int i = 0; i < array.Length; i++)
            {  
                if (resetMode == 0)
                {
                    array[i] = (byte)0;
                }else if (resetMode == 1)
                {
                    array[i] = (byte)rnd.Next();
                }else if (resetMode == 2 && rnd.Next() % 2 == 0)
                {
                    array[i] = (byte)rnd.Next();
                }
            }
            string after = Convert.ToBase64String(array);
            this.Log(LogLevel.Debug, "{0}: {1} -> {2}", devid, before, after);
        }

        public ushort ReadWord(long offset)
        {
            if(!IsCorrectOffset(offset, sizeof(ushort)))
            {
                return 0;
            }
            var intOffset = (int)offset;
            var result = BitConverter.ToUInt16(array, intOffset);
            return result;
        }

        public virtual void WriteWord(long offset, ushort value)
        {
            if(!IsCorrectOffset(offset, sizeof(ushort)))
            {
                return;
            }
            var bytes = BitConverter.GetBytes(value);
            bytes.CopyTo(array, offset);
        }

        public byte ReadByte(long offset)
        {
            if(!IsCorrectOffset(offset, sizeof(byte)))
            {
                return 0;
            }
            var intOffset = (int)offset;
            var result = array[intOffset];
            return result;
        }

        public virtual void WriteByte(long offset, byte value)
        {
            if(!IsCorrectOffset(offset, sizeof(byte)))
            {
                return;
            }
            var intOffset = (int)offset;
            array[intOffset] = value;
        }

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            if(!IsCorrectOffset(offset, count))
            {
                return new byte[count];
            }
            var result = new byte[count];
            Array.Copy(array, offset, result, 0, count);
            return result;
        }

        public void WriteBytes(long offset, byte[] bytes, int startingIndex, int count, IPeripheral context = null)
        {
            if(!IsCorrectOffset(offset, count))
            {
                return;
            }
            Array.Copy(bytes, startingIndex, array, offset, count);
        }

        public void LoadFileChunks(string path, IEnumerable<FileChunk> chunks, IPeripheral cpu)
        {
            this.LoadFileChunks(chunks, cpu);
        }

        public long Size
        {
            get
            {
                return array.Length;
            }
        }

        // ArrayMemory matches the host endianness because host-endian BitConverter operations are used for
        // accesses wider than a byte.
        public Endianess Endianness => BitConverter.IsLittleEndian ? Endianess.LittleEndian : Endianess.BigEndian;

        protected readonly byte[] array;
        public readonly uint devid;

        private bool IsCorrectOffset(long offset, int size)
        {
            var result = offset >= 0 && offset <= array.Length - size;
            if(!result)
            {
                this.Log(LogLevel.Error, "Tried to read {0} byte(s) at offset 0x{1:X} outside the range of the peripheral 0x0 - 0x{2:X}", size, offset, array.Length - 1);
            }
            return result;
        }

        // Objects bigger than 2GB are supported in .NET Framework with `gcAllowVeryLargeObjects`
        // enabled and in .NET by default but there can be no more elements than that in a single
        // dimension of an array. We could, e.g., double it by using more dimensions but generally
        // ArrayMemory is mostly intended to be used for memory smaller than page size, which is
        // required by MappedMemory, so this is much more than should be needed for ArrayMemory.
        private const ulong MaxSize = 0x7FFFFFC7;
    }
}
