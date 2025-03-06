//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.DMA

// this one has only 6 channels (0-5)
{
    public class MSP430FR5X_DMA : IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IGPIOReceiver, INumberedGPIOOutput, IDMA
    {
        public MSP430FR5X_DMA(IMachine machine, int NumberOfChannels) : base(machine)
        {
            engine = new DmaEngine(machine.GetSystemBus(this));
            channels = new Channel[NumberOfChannels];
            channelFinished = new bool[NumberOfChannels];
            var registerMap = DefineRegisters();
            for(var i = 0; i < NumberOfChannels; ++i)
            {
                channels[i] = new Channel(this, i, baseAddress);
            }
            registers = new WordRegisterCollection(this,registerMap);
            Reset();
        }

        private readonly DmaEngine engine;
        private readonly WordRegisterCollection registers;
        private readonly Channel[] channels;


        public void Reset()
        {
            registers.Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            return;
        }

        public ushort ReadWord(long offset)
        {
            return 0;
        }

        public void WriteWord(long offset, ushort value)
        {
            return;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0 || number > channels.Length)
            {
                this.Log(LogLevel.Error, "Channel number {0} is out of range, must be in [1; {1}]", number, channels.Length);
                return;
            }

            if(!value)
            {
                return;
            }

            this.Log(LogLevel.Debug, "DMA peripheral request on channel {0}", number);
            if(!channels[number - 1].TryTriggerTransfer())
            {
                this.Log(LogLevel.Warning, "DMA peripheral request on channel {0} ignored - channel is disabled "
                    + "or has data count set to 0", number);
            }
        }

        // each channel is only 0x10 bits
        private const int ChannelSize = (int)((long)Registers.Channel0Control - (long)Registers.Channel1Control); 
        private sealed class Channel
        {
            public Channel(MSP430FR5X_DMA parent, int number)
            {
                this.parent = parent;
                addressingMode = AddressingMode.Fixed2Fixed;
                transferType = TransferMode.SingleTransfer;
                channel = number;
                
                var registersMap = BuildRegisters();
                registers = new WordRegisterCollection(parent,registersMap);
            }

            private Dictionary<long, WordRegister> BuildRegisters(){
                var registersMap = new Dictionary<long, WordRegister>();

                registersMap.Add((long)ChannelRegisters.DMAxCTL + (channel * ChannelSize), new WordRegister(parent)
                    .WithFlag(0, out dmaReq, name:"DMA" + (channel.toString()) + "REQ")
                    .WithFlag(1, out dmaAbort, name:"DMA" + (channel.toString()) + "ABORT")
                    .WithFlag(2, out interruptEnable, name:"DMA" + (channel.toString()) + "IE")
                    .WithFlag(3, out interruptFlag, name:"DMA" + (channel.toString()) + "IFG")
                    .WithFlag(4, out enable, name:"DMA" + (channel.toString()) + "EN")
                    .WithFlag(5, out level, name:"DMA" + (channel.toString()) + "LEVEL")
                    .WithFlag(6, out sourceByte, name:"DMA" + (channel.toString()) + "SRCBYTE")
                    .WithFlag(7, out destByte, name:"DMA" + (channel.toString()) + "DSTBYTE")
                    .WithEnumField(8, 2, out srcIncr, name:"DMA" + (channel.toString()) + "SRCINC")
                    .WithEnumField(10, 2, out destIncr, name:"DMA" + (channel.toString()) + "DSTINCR")
                    .WithEnumField(12, 2, out triggerAssignment, name:"DMA" + (channel.toString()) + "DT")
                );

                registersMap.Add((long)ChannelRegisters.DMAxSAL + (channel * ChannelSize), new WordRegister(parent)
                    .WithValueField(0,16,out srcLow, name:"DMA" + (channel.toString()) + "SAL")
                );
                registersMap.Add((long)ChannelRegisters.DMAxSAH + (channel * ChannelSize), new WordRegister(parent)
                    .WithValueField(0,4,out srcHigh, name:"DMA" + (channel.toString()) + "SAH")
                    .WithReservedBits(5,12)
                );

                registersMap.Add((long)ChannelRegisters.DMAxDAH + (channel * ChannelSize), new WordRegister(parent)
                    .WithValueField(0,16,out destLow, name:"DMA" + (channel.toString()) + "DAL")
                );
                registersMap.Add((long)ChannelRegisters.DMAxDAH + (channel * ChannelSize), new WordRegister(parent)
                    .WithValueField(0,4,out destHigh, name:"DMA" + (channel.toString()) + "DAH")
                    .WithReservedBits(5,12)
                );

                registersMap.Add((long)ChannelRegisters.DMAxSZ + (channel * ChannelSize), new WordRegister(parent)
                    .WithValueField(0,16,out dmaSize, 
                        WriteCallBack: (_,value) => initializedSize = (uint)value)
                );
                return registersMap;
            }

            public GPIO IRQ { get; private set; }

            public void Reset()
            {
                IRQ.Unset();
                peripheralIncrement = false;
                peripheralAddress = 0u;
                memoryAddress = 0u;
                memoryIncrement = false;
                memoryTransferType = 0;
                peripheralTransferType = 0;
                completeInterruptEnabled = false;
                transferErrorInterruptEnabled = false;
                numberOfData = 0;
                priority = 0;
            }

            public bool TryTriggerTransfer()
            {
                if(!Enabled || dataCount.Value == 0)
                {
                    return false;
                }

                DoTransfer();
                parent.Update();
                return true;
            }

            private uint HandleConfigurationRead()
            {
                var returnValue = 0u;
                returnValue |= completeInterruptEnabled ? (1u << 1) : 0u;
                returnValue |= transferErrorInterruptEnabled ? (1u << 3) : 0u;
                returnValue |= peripheralIncrement ? (1u << 6) : 0u;
                returnValue |= memoryIncrement ? (1u << 7) : 0u;
                returnValue |= (uint)(priority << 12);
                return returnValue;
            }

            private void HandleConfigurationWrite(uint value)
            {
                completeInterruptEnabled = (value & (1 << 1)) != 0;
                transferErrorInterruptEnabled = (value & (1 << 3)) != 0;
                peripheralIncrement = (value & (1 << 6)) != 0;
                memoryIncrement = (value & (1 << 7)) != 0;
                priority = (byte)((value >> 12) & 3);

                if((value & ~0x30DB) != 0)
                {
                    parent.Log(LogLevel.Warning, "Channel {0}: some unhandled bits were written to configuration register. Value is 0x{1:X}.", channel, value);
                }

                if((value & 1) != 0)
                {
                    DoTransfer();
                }
            }

            private void DoTransfer()
            {
                uint sourceAddress, destinationAddress;
                bool incrementSourceAddress, incrementDestinationAddress;
                TransferType sourceTransferType, destinationTransferType;

                if(direction == Direction.ReadFromMemory)
                {
                    sourceAddress = memoryAddress;
                    destinationAddress = peripheralAddress;
                    incrementSourceAddress = memoryIncrement;
                    incrementDestinationAddress = peripheralIncrement;
                    sourceTransferType = memoryTransferType;
                    destinationTransferType = peripheralTransferType;
                }
                else
                {
                    sourceAddress = peripheralAddress;
                    destinationAddress = memoryAddress;
                    incrementSourceAddress = peripheralIncrement;
                    incrementDestinationAddress = memoryIncrement;
                    sourceTransferType = peripheralTransferType;
                    destinationTransferType = memoryTransferType;
                }
               
                var request = new Request(sourceAddress, destinationAddress, numberOfData, sourceTransferType, destinationTransferType,
                                  incrementSourceAddress, incrementDestinationAddress);
                parent.engine.IssueCopy(request);
                IRQ.Set();
            }

            private bool memoryIncrement;
            private bool peripheralIncrement;
            private uint peripheralAddress;
            private uint memoryAddress;
            private TransferType memoryTransferType;
            private TransferType peripheralTransferType;
            private bool completeInterruptEnabled;
            private bool transferErrorInterruptEnabled;
            private int numberOfData;
            private byte priority;

            private IFlagRegisterField dmaReq;
            private IFlagRegisterField dmaAbort;
            private IFlagRegisterField interruptEnable;
            private IFlagRegisterField interruptFlag;
            private IFlagRegisterField enable;
            private IFlagRegisterField level;
            private IFlagRegisterField sourceByte;
            private IFlagRegisterField destByte;
            private IEnumRegisterField<AddressChange> srcIncr;
            private IEnumRegisterField<AddressChange> destIncr;
            private IEnumRegisterField<TriggerAssignments> triggerAssignment;

            private ushort srcLow;
            private ushort srcHigh;
            private uint sa => (uint)srclow | ((uint)srcHigh << 16);
            private ushort destLow;
            private ushort destHigh;
            private uint da => (uint)destlow | ((uint)destHigh << 16);

            private uint dmaSize;

            private uint initializedSA;
            private uint initializedDA;
            private uint initializedSize;
            
            private readonly MSP430FR5X_DMA parent;
            private readonly int channel;
            private readonly WordRegisterCollection registers;

            private enum AddressChange
            {
                unchanged = 0,
                unchanged1 = 1,
                decremented = 2,
                incremented = 3
            }

            private enum TransferMode
            {
                SingleTransfer = 0x0,
                BlockTransfer = 0x1, 
                BurstBlockTransfer = 0x2,
                BurstBlockTransfer1 = 0x3,
                RepeatedSingleTransfer = 0x4,
                RepeatedBlockTransfer = 0x05,
                RepeatedBurstTransfer = 0x6,
                RepeatedBurstTransfer1 = 0x7
            }

            private enum TriggerAssignments
            {
                DMAREQ = 0,
                TA0CCR0CCIFG = 1,
                TA0CCR2CCIFG = 2,
                TA1CCR0CCIFG = 3,
                TA1CCR2CCIFG = 4,
                TA2CCR0CCIFG = 5,
                TA3CCR0CCIFG = 6,
                TB0CCR0CCIFG = 7,
                TB0CCR2CCIFG = 8,
                TA4CCR0CCIFG = 9,

                AESTrigger0 = 11,
                AESTrigger1 = 12,
                AESTrigger2 = 13,

                UCA0RXIFG = 14,
                UCA0TXIFG = 15,
                UCA1RXIFG = 16,
                UCA1TXIFG = 17,

                UCB0RXIFG0 = 18,
                UCB0TXIFG0 = 19,
                UCB0RXIFG1 = 20,
                UCB0TXIFG1 = 21,
                UCB0RXIFG2 = 22,
                UCB0TXIFG2 = 23,
                UCB0RXIFG3 = 24,
                UCB0TXIFG3 = 25,

                ADC12 = 26,

                //LEAReady = 27, // FR599X 

                MPYREADY = 29,
                DMA2IFG = 30,
                DMAE0 = 31
            }

            private enum ChannelRegisters
            {
                DMAxCTL = 0x0,
                DMAxSAL = 0x2,
                DMAxSAH = 0x4,
                DMAxDAL = 0x6,
                DMAxDAH = 0x8,
                DMAxSZ = 0xA
            }
        }
        private void SetTrigger(byte trigger, int channel)
        {
            if (channel < channels.length)
            {
                channels[channel].triggerAssignment = (int)trigger;
            }
        }

        private IFlagRegisterField enableNMI;
        private IFlagRegisterField roundRobin;
        private IFlagRegisterField rmwdis;

        private IEnumRegisterField<InterruptStatus> GlobalInterrupt;
        // this is the control registers and the interrupt vector
        private void DefineRegisters()
        {
            Registers.Control0.Define(this)
                .withValueField(0,5, name: "DMA0TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,0))
                .WithReservedBits(5,3)
                .withValueField(0,5, name: "DMA1TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,1))
                .WithReservedBits(13,3)
            ;
            Registers.Control1.Define(this)
                .withValueField(0,5, name: "DMA2TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,2))
                .WithReservedBits(5,3)
                .withValueField(0,5, name: "DMA3TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,3))
                .WithReservedBits(13,3)
            ;
            Registers.Control2.Define(this)
                .withValueField(0,5, name: "DMA4TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,4))
                .WithReservedBits(5,3)
                .withValueField(0,5, name: "DMA5TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,5))
                .WithReservedBits(13,3)
            ;
            Registers.Control3.Define(this)
                .withValueField(0,5, name: "DMA6TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,6))
                .WithReservedBits(5,3)
                .withValueField(0,5, name: "DMA7TSEL", 
                    WriteCallBack: (_,value) => SetTrigger((byte)value,7))
                .WithReservedBits(13,3)
            ;
            Registers.Control4.Define(this)
                .withFlag(0, out enableNMI, name:"ENNMI")
                .WithFlag(1, out roundRobin, name:"ROUNDROBIN")
                .WithFlag(2, out rmwdis, name:"DMARMWDIS")
                .WithReservedBits(3,13)
            ;

            Registers.Interrupt.Define(this)
                .withEnumField(0,16, out GlobalInterrupt)
            ;
        }

        // I think this has to be relative to the entire machine even though each channel is
        // also has its own relative channel registers
        private enum Registers
        {
            Control0 = 0x0,
            Control1 = 0x2,
            Control2 = 0x4,
            Control3 = 0x6,
            Control4 = 0x8,
            Interupt = 0xE,
            // Channel 0
            Channel0Control = 0x10,
            Channel0SourceLow = 0x12,
            Channel0SourceHigh = 0x14,
            Channel0DestLow = 0x16,
            Channel0DestHigh = 0x18,
            Channel0TransferSize = 0x1A,
            // Channel 1
            Channel1Control = 0x20,
            Channel1SourceLow = 0x22,
            Channel1SourceHigh = 0x24,
            Channel1DestLow = 0x26,
            Channel1DestHigh = 0x28,
            Channel1TransferSize = 0x2A,
            // Channel 2
            Channel2Control = 0x20,
            Channel2SourceLow = 0x22,
            Channel2SourceHigh = 0x24,
            Channel2DestLow = 0x26,
            Channel2DestHigh = 0x28,
            Channel2TransferSize = 0x2A,
            // Channel 3
            Channel3Control = 0x30,
            Channel3SourceLow = 0x32,
            Channel3SourceHigh = 0x34,
            Channel3DestLow = 0x36,
            Channel3DestHigh = 0x38,
            Channel3TransferSize = 0x3A,
            // Channel 4
            Channel4Control = 0x40,
            Channel4SourceLow = 0x42,
            Channel4SourceHigh = 0x44,
            Channel4DestLow = 0x46,
            Channel4DestHigh = 0x48,
            Channel4TransferSize = 0x4A,
            // Channel 5
            Channel5Control = 0x50,
            Channel5SourceLow = 0x52,
            Channel5SourceHigh = 0x54,
            Channel5DestLow = 0x56,
            Channel5DestHigh = 0x58,
            Channel5TransferSize = 0x5A,
            // Channel 6
            Channel6Control = 0x60,
            Channel6SourceLow = 0x62,
            Channel6SourceHigh = 0x64,
            Channel6DestLow = 0x66,
            Channel6DestHigh = 0x68,
            Channel6TransferSize = 0x6A

        }

        private enum InterruptStatus
        {
            None = 0,
            Channel0Interrupt = 0x2,
            Channel1Interrupt = 0x4,
            Channel2Interrupt = 0x6,
            Channel3Interrupt = 0x8,
            Channel4Interrupt = 0xA,
            Channel5Interrupt = 0xC,
            Channel6Interrupt = 0xE,
            Channel7Interrupt = 0x10
        }
    }
    
}
