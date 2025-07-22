using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Timers
{
    [AllowedTranslations(AllowedTranslation.ByteToWord)]
    public class MSP430FR59XX_Watchdog : BasicWordPeripheral, IKnownSize
    {
        public MSP430FR59XX_Watchdog(IMachine machine, long baseFrequency) : base(machine)
        {
            mainTimer = new LimitTimer(machine.ClockSource, baseFrequency, this, "wdt", limit: 0xFFFF, workMode: WorkMode.Periodic);
            mainTimer.LimitReached += LimitReached;

            InterruptEnableRegister = new ByteRegister(this);
            InterruptStatusRegister = new ByteRegister(this);

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();

            mainTimer.Reset();
            UpdateLimit(Interval.Default);
        }

        [ConnectionRegionAttribute("interruptEnable")]
        public void WriteByteToInterruptEnable(long offset, byte value)
        {
            if(offset != 0)
            {
                this.Log(LogLevel.Warning, "Illegal write access at non-zero offset (0x{0:X}) to interruptEnable region", offset);
                return;
            }
            // NOTE: This region is single byte wide, so we are ignoring offset argument
            InterruptEnableRegister.Write(0, value);
        }

        [ConnectionRegionAttribute("interruptEnable")]
        public byte ReadByteFromInterruptEnable(long offset)
        {
            if(offset != 0)
            {
                this.Log(LogLevel.Warning, "Illegal read access at non-zero offset (0x{0:X}) to interruptEnable region", offset);
            }
            // NOTE: This region is single byte wide, so we are ignoring offset argument
            return InterruptEnableRegister.Read();
        }

        [ConnectionRegionAttribute("interruptStatus")]
        public void WriteByteToInterruptStatus(long offset, byte value)
        {
            if(offset != 0)
            {
                this.Log(LogLevel.Warning, "Illegal write access at non-zero offset (0x{0:X}) to interruptStatus region", offset);
                return;
            }
            // NOTE: This region is single byte wide, so we are ignoring offset argument
            InterruptStatusRegister.Write(0, value);
            UpdateInterrupts();
        }

        [ConnectionRegionAttribute("interruptStatus")]
        public byte ReadByteFromInterruptStatus(long offset)
        {
            if(offset != 0)
            {
                this.Log(LogLevel.Warning, "Illegal read access at non-zero offset (0x{0:X}) to interruptStatus region", offset);
            }
            // NOTE: This region is single byte wide, so we are ignoring offset argument
            return InterruptStatusRegister.Read();
        }

        public long Size => 0x02;

        public GPIO IntervalIRQ { get; } = new GPIO();

        private void UpdateInterrupts()
        {
            var interrupt = intervalInterruptPending.Value && intervalInterruptEnabled.Value;
            IntervalIRQ.Set(interrupt);
        }

        private void LimitReached()
        {
            if(intervalMode.Value)
            {
                intervalInterruptPending.Value = true;
                UpdateInterrupts();
                return;
            }

            machine.RequestReset();
        }

        private void DefineRegisters()
        {
            Registers.Control.Define(this, 0x6900)
                .WithEnumField<WordRegister, Interval>(0, 3, name: "WDTIS",
                    changeCallback: (_, value) => UpdateLimit(value))
                // NOTE: Change of clock source is not supported in runtime
                .WithFlag(3, FieldMode.Read | FieldMode.WriteOneToClear, name: "WDTCNTCL",
                    writeCallback: (_, value) => mainTimer.Value = 0)
                .WithFlag(4, out intervalMode, name: "WDTTMSEL")
                .WithValueField(5, 2, name: "WDTSSEL")
                .WithFlag(7, name: "WDTHOLD",
                    changeCallback: (_, value) => mainTimer.Enabled = !value)
                .WithValueField(8, 8, name: "WDTPW",
                    valueProviderCallback: _ => 0x69,
                    writeCallback: (_, value) =>
                    {
                        if(value != WatchdogPassword)
                        {
                            machine.RequestReset();
                        }
                    })
            ;
        }

        private void UpdateLimit(Interval interval)
        {
            switch(interval)
            {
                case Interval._2147483648:
                    mainTimer.Limit = 2147483648;
                    break;

                case Interval._134217728:
                    mainTimer.Limit = 134217728;
                    break;

                case Interval._8388608:
                    mainTimer.Limit = 8388608;
                    break;

                case Interval._524288:
                    mainTimer.Limit = 524288;
                    break;

                case Interval._32768:
                    mainTimer.Limit = 32768;
                    break;

                case Interval._8192:
                    mainTimer.Limit = 8192;
                    break;

                case Interval._512:
                    mainTimer.Limit = 512;
                    break;

                case Interval._64:
                    mainTimer.Limit = 64;
                    break;

                default:
                    throw new Exception("unreachable");
            }
        }
        private ByteRegister InterruptEnableRegister { get; }
        private ByteRegister InterruptStatusRegister { get; }


        private IFlagRegisterField intervalMode;
        private IFlagRegisterField intervalInterruptPending;
        private IFlagRegisterField intervalInterruptEnabled;

        private readonly LimitTimer mainTimer;

        private const uint WatchdogPassword = 0x5A;

        private enum Interval
        {
            _2147483648 = 0,
            _134217728,
            _8388608,
            _524288,
            _32768,
            _8192,
            _512,
            _64,
            Default = Interval._32768,
        }

        private enum Registers
        {
            Control = 0x00,
        }
    }
}
