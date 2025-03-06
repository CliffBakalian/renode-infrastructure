//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class MSP430F5XXX_MPY32 : BasicWordPeripheral, IBytePeripheral
    {
        public MSP430F5XXX_MPY32(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset == (long)Registers.MultiplySignedOperand1 || offset == (long)Registers.MultiplySignedAccumulateOperand1)
            {
                ushort extendedValue = value;
                extendedValue |= (value & 0x80) > 0 ? (ushort)0xFF00 : (ushort)0;
                WriteWord(offset, extendedValue);
                return;
            }

            WriteWord(offset, value);
        }

        public byte ReadByte(long offset)
        {
            return (byte)ReadWord(offset);
        }

        private void SetModeAndOperand(ushort value, Mode mode)
        {
            operand1 = (uint)value;
            currentMode = mode;
            op1Width_32 = false;
        }

        private void resetResultRegisters(bool full64bit)
        {
            res32_0.Value = 0;
            res32_1.Value = 0;
            resultLow.Value = 0;
            resultHigh.Value = 0;
            if (full64bit){
                res32_2.Value = 0;
                res32_3.Value = 0; 
            }
        }

        private void SetOperandHigh(ushort value, Mode mode)
        {
            if (currentMode == mode){
                op1Width_32 = true;
                operand1 |= (uint)(value << 16);
            }
        }

        private void Store16Result(uint result)
        {
            StoreResult((ulong)result,false);
        }

        private void StoreResult(ulong result, bool result32 = true){
            resultLow.Value = (ushort)result;
            res32_0.Value = (ushort)result;
            resultHigh.Value = (ushort)(result >> 16);
            res32_1.Value = (ushort)(result >> 16);
            if (result32){
                res32_2.Value = (ushort)(result >> 32);
                res32_3.Value = (ushort)(result >> 48);
            }            
        }

        private void SetMode(byte mode){
            switch(mode){
                case 0x00:{
                    currentMode = Mode.Unsigned;
                    break;
                }
                case 0x01:{
                    currentMode = Mode.Signed;
                    break;
                }
                case 0x02:{
                    currentMode = Mode.UnsignedAccumulate;
                    break;
                }
                case 0x03:{
                    currentMode = Mode.SignedAccumulate;
                    break;
                }
            }

        }
        private void saturate(bool nonfrac, bool allregs){
            if (allregs){
              saturate64(nonfrac);
            }else{
              saturate32(nonfrac);
            }
            //allregs ? saturate64(nonfrac) : saturate32(nonfrac);
        }

        private void setLetRegister(Registers r){
            lastRegisterBeforeOp2 = r;
        }
        private void saturate32(bool nonfrac)
        {
            if ((!carryFlag.Value) && ((res32_1.Value >> 15)==1)){
                Store16Result(0x7FFFFFFF);
            }else if ((carryFlag.Value) && ((res32_1.Value >> 15) == 0)){
                Store16Result(0x80000000);
            }else if (nonfrac || (fracMode.Value)){
                return;
            }else if ((ushort)(res32_1.Value >> 14) == 0x1){
                Store16Result(0x7FFFFFFF);
            }else if((ushort)(res32_1.Value >> 14) == 0x2){
                Store16Result(0x80000000);
            }
        }

        private void saturate64(bool nonfrac)
        {
            if ((carryFlag.Value) && ((res32_3.Value >> 15)==1)){
                StoreResult(0x7FFFFFFFFFFFFFFF);
            }else if (carryFlag.Value && ((res32_3.Value >> 15)==0)){
                StoreResult(0x8000000000000000);
            }else if (nonfrac || !fracMode.Value){
                return;
            }else if ((ushort)(res32_3.Value >> 14) == 0x1){
                StoreResult(0x7FFFFFFFFFFFFFFF);
            }else if((ushort)(res32_3.Value >> 14) == 0x2){
                StoreResult(0x8000000000000000);
            }
        }

        // allregs is basically asking if it's not backwards compatible with the the MPY unit.
        // allregs = true means do the 32x32 -> 64bit
        // allregs = false means do the 16x16 -> 32bit 
        private void PerformCalculation(uint op2, bool allregs = false)
        {
            switch(currentMode)
            {
                case Mode.Unsigned:
                {
                    resetResultRegisters(allregs);
                    var result = operand1 * (allregs? (uint)op2 : (ushort)op2);
                    StoreResult((ulong)result, allregs);
                    sumExtend.Value = 0;
                    carryFlag.Value = false;
                    break;
                }

                case Mode.Signed:
                {
                    resetResultRegisters(allregs);
                    var result = allregs ? ((int)operand1 * (int)op2) : ((short)operand1 * (short)op2);
                    StoreResult((ulong)result, allregs);
                    sumExtend.Value = result < 0 ? (allregs ? 0xFFFFFFFF : 0xFFFFU) : 0U;
                    carryFlag.Value = result < 0 ? true : false;
                    break;
                }

                case Mode.UnsignedAccumulate:
                {
                    if (satMode.Value){
                        saturate(true,allregs);
                    }

                    var result = (allregs ? Last64Result : Last32Result) + (allregs ? ((long)(operand1 * op2)) : ((int)(operand1 * op2)));
                    StoreResult((ulong)result, allregs);
                    sumExtend.Value = (result > (allregs ? uint.MaxValue : ushort.MaxValue)) ? 1U : 0U;
                    carryFlag.Value = (result > (allregs ? uint.MaxValue : ushort.MaxValue)) ? true : false;
                    break;
                }

                case Mode.SignedAccumulate:
                {
                    // this is what I thought but documentation gives a different flow diagram. Let's go with that
                    //int prev = satMode ? (carryFlag.Value == sumExtend.Value ? Last32Result : (sumExtend.Value == 0 ? int.MinValue : int.MaxValue)) : Last32Result;
                    //store16Result((short)prev);
                    if (satMode.Value){
                        saturate(true,allregs);
                    }

                    var result = (allregs ? Last64Result : Last32Result) + (allregs ? ((short)operand1 * (short)op2) : ((int)operand1 * (int)op2));
                    StoreResult((ulong)result, allregs);
                    sumExtend.Value = result < 0 ? (allregs ? 0xFFFFFFFF : 0xFFFFU) : 0U;
                    carryFlag.Value = result > (allregs ? uint.MaxValue : ushort.MaxValue) ? true : false;
                    break;
                }
            }

            if (fracMode.Value){
                // I think just change sumext based on carry flag. TODO
            }
            if (satMode.Value){
                saturate(false,allregs);
                satMode.Value = false;
            }
        }

        private void PerformCalcAndSetRegister(uint op2, bool allregs = false){
            if(lastRegisterBeforeOp2 == Registers.Operand2Low){
                var newoperand = (uint)((ushort)op2 << 16 | (ushort)op2field.Value);
                PerformCalculation(newoperand,allregs);
            }
        }
        private void DefineRegisters()
        {
            Registers.MultiplyUnsignedOperand1.Define(this)
                .WithValueField(0, 16, name: "MPY",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value, Mode.Unsigned))
            ;

            Registers.MultiplySignedOperand1.Define(this)
                .WithValueField(0, 16, name: "MPYS",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value, Mode.Signed))
                ;

            Registers.MultiplyAccumulateOperand1.Define(this)
                .WithValueField(0, 16, name: "MAC",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value, Mode.UnsignedAccumulate))
            ;

            Registers.MultiplySignedAccumulateOperand1.Define(this)
                .WithValueField(0, 16, name: "MACS",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value, Mode.SignedAccumulate))
            ;

            Registers.SecondOperand.Define(this)
                .WithValueField(0, 16, name: "OP2",
                    writeCallback: (_, value) => PerformCalculation((uint)value, false))
            ;

            Registers.ResultLow.Define(this)
                .WithValueField(0, 16, out resultLow, name: "RESLO")
            ;

            Registers.ResultHigh.Define(this)
                .WithValueField(0, 16, out resultHigh, name: "RESHI")
            ;

            Registers.SumExtend.Define(this)
                .WithValueField(0, 16, out sumExtend, FieldMode.Read, name: "SUMEXT")
            ;

            Registers.MultiplyUnsignedLowOp1.Define(this)
                .WithValueField(0, 16, name: "MPY32L",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value,Mode.Unsigned))
            ;

            Registers.MultiplySignedLowOp1.Define(this)
                .WithValueField(0, 16, name: "MPYS32L",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value,Mode.Signed))
            ;

            Registers.MultiplyAccumulateLowOp1.Define(this)
                .WithValueField(0, 16, name: "MAC32L",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value,Mode.UnsignedAccumulate))
            ;

            Registers.MultiplySignedAccumulateLowOp1.Define(this)
                .WithValueField(0, 16, name: "MACS32L",
                    writeCallback: (_, value) => SetModeAndOperand((ushort)value,Mode.SignedAccumulate))
            ;

            //############################### HIGH STUFF ##############################3

            Registers.MultiplyUnsignedHighOp1.Define(this)
                .WithValueField(0, 16, name: "MPY32H",
                    writeCallback: (_, value) => SetOperandHigh((ushort)value,Mode.Unsigned))
            ;

            Registers.MultiplySignedHighOp1.Define(this)
                .WithValueField(0, 16, name: "MPYS32H",
                    writeCallback: (_, value) => SetOperandHigh((ushort)value,Mode.Signed))
            ;

            Registers.MultiplyAccumulateHighOp1.Define(this)
                .WithValueField(0, 16, name: "MAC32H",
                    writeCallback: (_, value) => SetOperandHigh((ushort)value,Mode.UnsignedAccumulate))
            ;

            Registers.MultiplySignedAccumulateHighOp1.Define(this)
                .WithValueField(0, 16, name: "MACS32H",
                    writeCallback: (_, value) => SetOperandHigh((ushort)value,Mode.SignedAccumulate))
            ;

            Registers.Operand2Low.Define(this)
                .WithValueField(0, 16, out op2field, name: "OP2L",
                //.WithValueField(0, 16, out operand2, name: "OP2L")//, 
                    writeCallback: (_x,_y) => lastRegisterBeforeOp2 = Registers.Operand2Low)
            ;

            Registers.Operand2High.Define(this)
                .WithValueField(0,16, name: "OP2H",
                    writeCallback: (_,value) => PerformCalcAndSetRegister((uint)value,true))//PerformCalculation((uint)value,true)) // lastRegisterBeforeOp2 == Registers.Operand2Low 
            ;

            Registers.RES32_0.Define(this)
                .WithValueField(0, 16, out res32_0, name: "RES0")
            ;
            
            Registers.RES32_1.Define(this)
                .WithValueField(0, 16, out res32_1, name: "RES1")
            ;
            
            Registers.RES32_2.Define(this)
                .WithValueField(0, 16, out res32_2, name: "RES2")
            ;
            
            Registers.RES32_3.Define(this)
                .WithValueField(0, 16, out res32_3, name: "RES3")
            ;

            Registers.Control.Define(this)
                .WithFlag(0, out carryFlag, name: "MPYC")
                .WithReservedBits(1,1)
                .WithFlag(2, out fracMode, name: "MPYFRAC")
                .WithFlag(3, out satMode, name: "MPYSAT")
                .WithValueField(4,2, name: "MPYMx", 
                    writeCallback: (_,value) => SetMode((byte)value))
                .WithTaggedFlag("MPYOP1_32", 6)
                .WithTaggedFlag("MPYOP2_32", 7)
                .WithTaggedFlag("MPYDLYWRTEN", 8)
                .WithTaggedFlag("MPYDLY32", 9)
                .WithReservedBits(10,6)
            ;
        }

        private int Last32Result => (int)res32_0.Value | ((int)res32_1.Value << 16);
        private long Last64Result => (long)res32_0.Value | ((long)res32_1.Value << 16) | ((long)res32_2.Value <<32) | ((long)res32_3.Value << 48);
        private Mode currentMode;

        private Registers lastRegisterBeforeOp2;
        private uint operand1;
        private uint operand2;
        private IValueRegisterField op2field;
        private bool op1Width_32; // false = 16bit, true = 32bit 

        private IValueRegisterField resultLow;
        private IValueRegisterField resultHigh;

        private IValueRegisterField res32_0;
        private IValueRegisterField res32_1;
        private IValueRegisterField res32_2;
        private IValueRegisterField res32_3;

        private IValueRegisterField sumExtend;

        private IFlagRegisterField carryFlag;
        private IFlagRegisterField fracMode;
        private IFlagRegisterField satMode;

        private enum Mode
        {
            Unsigned,
            Signed,
            UnsignedAccumulate,
            SignedAccumulate,
        }

        private enum Registers
        {
            MultiplyUnsignedOperand1 = 0x00,
            MultiplySignedOperand1 = 0x02,
            MultiplyAccumulateOperand1 = 0x04,
            MultiplySignedAccumulateOperand1 = 0x06,
            SecondOperand = 0x8,
            Operand2Low = 0x20, 
            Operand2High = 0x22,
            ResultLow = 0xA,
            ResultHigh = 0xC,
            SumExtend = 0xE,
            MultiplyUnsignedLowOp1 = 0x10,
            MultiplyUnsignedHighOp1 = 0x12,
            MultiplySignedLowOp1 = 0x14,
            MultiplySignedHighOp1 = 0x16,
            MultiplyAccumulateLowOp1 = 0x18,
            MultiplyAccumulateHighOp1 = 0x1A,
            MultiplySignedAccumulateLowOp1 = 0x1C,
            MultiplySignedAccumulateHighOp1 = 0x1E,
            RES32_0 = 0x24,
            RES32_1 = 0x26,
            RES32_2 = 0x28,
            RES32_3 = 0x2A,
            Control = 0x2C
        }
    }
}
