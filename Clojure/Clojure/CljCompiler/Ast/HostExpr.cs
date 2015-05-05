﻿/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

/**
 *   Author: David Miller
 **/

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace clojure.lang.CljCompiler.Ast
{
    public abstract class HostExpr : Expr, MaybePrimitiveExpr
    {
        #region Symbols

        public static readonly Symbol ByRefSym = Symbol.intern("by-ref");
        public static readonly Symbol TypeArgsSym = Symbol.intern("type-args");

        #endregion

        #region Parsing

        public sealed class Parser : IParser
        {
            public Expr Parse(ParserContext pcon, object form)
            {
                ISeq sform = (ISeq)form;

                // form is one of:
                //  (. x fieldname-sym)
                //  (. x 0-ary-method)
                //  (. x propertyname-sym)
                //  (. x methodname-sym args)+
                //  (. x (methodname-sym args?))
                //  (. x (generic-m 

                if (RT.Length(sform) < 3)
                    throw new ParseException("Malformed member expression, expecting (. target member ... )");

                string source = (string)Compiler.SourceVar.deref();
                IPersistentMap spanMap = (IPersistentMap)Compiler.SourceSpanVar.deref();  // Compiler.GetSourceSpanMap(form);

                Symbol tag = Compiler.TagOf(sform);

                // determine static or instance
                // static target must be symbol, either fully.qualified.Typename or Typename that has been imported
                 
                Type t = HostExpr.MaybeType(RT.second(sform), false);
                // at this point, t will be non-null if static

                Expr instance = null;
                if (t == null)
                    instance = Compiler.Analyze(pcon.EvalOrExpr(),RT.second(sform));

                bool isZeroArityCall = RT.Length(sform) == 3 && RT.third(sform) is Symbol;

                if (isZeroArityCall)
                {
                    PropertyInfo pinfo = null;
                    FieldInfo finfo = null;

                    // TODO: Figure out if we want to handle the -propname otherwise.

                    bool isPropName = false;
                    Symbol sym = (Symbol)RT.third(sform);
                    if (sym.Name[0] == '-')
                    {
                        isPropName = true;
                        sym = Symbol.intern(sym.Name.Substring(1));
                    }

                    string fieldName = Compiler.munge(sym.Name);
                    // The JVM version does not have to worry about Properties.  It captures 0-arity methods under fields.
                    // We have to put in special checks here for this.
                    // Also, when reflection is required, we have to capture 0-arity methods under the calls that
                    //   are generated by StaticFieldExpr and InstanceFieldExpr.
                    if (t != null)
                    {
                        if ((finfo = Reflector.GetField(t, fieldName, true)) != null)
                            return new StaticFieldExpr(source, spanMap, tag, t, fieldName, finfo);
                        if ((pinfo = Reflector.GetProperty(t, fieldName, true)) != null)
                            return new StaticPropertyExpr(source, spanMap, tag, t, fieldName, pinfo);
                        if (!isPropName && Reflector.GetArityZeroMethod(t, fieldName, true) != null)
                            return new StaticMethodExpr(source, spanMap, tag, t, fieldName, null, new List<HostArg>());
                        throw new MissingMemberException(t.Name, fieldName);
                    }
                    else if (instance != null)
                    {
                        Type instanceType = (instance.HasClrType && instance.ClrType != null) ? instance.ClrType : typeof(object);
                        
                        if ((finfo = Reflector.GetField(instanceType, fieldName, false)) != null) {
                            return new InstanceFieldExpr(source, spanMap, tag, instance, fieldName, finfo);
                        }
                        if ((pinfo = Reflector.GetProperty(instanceType, fieldName, false)) != null) {
                            return new InstancePropertyExpr(source, spanMap, tag, instance, fieldName, pinfo);
                        }
                        if (!isPropName && Reflector.GetArityZeroMethod(instanceType, fieldName, false) != null)  {
                            return new InstanceMethodExpr(source, spanMap, tag, instance, fieldName, null, new List<HostArg>());
                        }
                        if (pcon.IsAssignContext) {
                            // Console.WriteLine("D");
                            return new InstanceFieldExpr(source, spanMap, tag, instance, fieldName, null); // same as InstancePropertyExpr when last arg is null
                        }
                        else
                            return new InstanceZeroArityCallExpr(source, spanMap, tag, instance, fieldName);
                    }
                    else
                    {
                        //  t is null, so we know this is not a static call
                        //  If instance is null, we are screwed anyway.
                        //  If instance is not null, then we don't have a type.
                        //  So we must be in an instance call to a property, field, or 0-arity method.
                        //  The code generated by InstanceFieldExpr/InstancePropertyExpr with a null FieldInfo/PropertyInfo
                        //     will generate code to do a runtime call to a Reflector method that will check all three.
                        //return new InstanceFieldExpr(source, spanMap, tag, instance, fieldName, null); // same as InstancePropertyExpr when last arg is null
                        //return new InstanceZeroArityCallExpr(source, spanMap, tag, instance, fieldName); 
                        if (pcon.IsAssignContext)
                            return new InstanceFieldExpr(source, spanMap, tag, instance, fieldName, null); // same as InstancePropertyExpr when last arg is null
                        else
                            return new InstanceZeroArityCallExpr(source, spanMap, tag, instance, fieldName); 

                    }
                }
 
                //ISeq call = RT.third(form) is ISeq ? (ISeq)RT.third(form) : RT.next(RT.next(form));

                ISeq call;
                List<Type> typeArgs = null;

                object fourth = RT.fourth(sform);
                if (fourth is ISeq && RT.first(fourth) is Symbol && ((Symbol)RT.first(fourth)).Equals(TypeArgsSym))
                 {
                    // We have a type args supplied for a generic method call
                    // (. thing methodname (type-args type1 ... ) args ...)
                    typeArgs = ParseGenericMethodTypeArgs(RT.next(fourth));
                    call = RT.listStar(RT.third(sform), RT.next(RT.next(RT.next(RT.next(sform)))));
                }
                else
                    call = RT.third(sform) is ISeq ? (ISeq)RT.third(sform) : RT.next(RT.next(sform));

                if (!(RT.first(call) is Symbol))
                    throw new ParseException("Malformed member exception");

                string methodName = Compiler.munge(((Symbol)RT.first(call)).Name);

                List<HostArg> args = ParseArgs(pcon, RT.next(call));

                return t != null
                    ? (MethodExpr)(new StaticMethodExpr(source, spanMap, tag, t, methodName, typeArgs, args))
                    : (MethodExpr)(new InstanceMethodExpr(source, spanMap, tag, instance, methodName, typeArgs, args));
            }
        }

        static List<Type> ParseGenericMethodTypeArgs(ISeq targs)
        {
            List<Type> types = new List<Type>();

            for (ISeq s = targs; s != null; s = s.next())
            {
                object arg = s.first();
                if (!(arg is Symbol))
                    throw new ArgumentException("Malformed generic method designator: type arg must be a Symbol");
                Type t = HostExpr.MaybeType(arg, false);
                if (t == null)
                    throw new ArgumentException("Malformed generic method designator: invalid type arg");
                types.Add(t);
            }

            return types;
        }

        internal static List<HostArg> ParseArgs(ParserContext pcon, ISeq argSeq)
        {
            List<HostArg> args = new List<HostArg>();

            for (ISeq s = argSeq; s != null; s = s.next())
            {
                object arg = s.first();

                HostArg.ParameterType paramType = HostArg.ParameterType.Standard;
                LocalBinding lb = null;

                ISeq argAsSeq = arg as ISeq;
                if (argAsSeq != null)
                {
                    Symbol op = RT.first(argAsSeq) as Symbol;
                    if (op != null && op.Equals(ByRefSym))
                    {
                        if (RT.Length(argAsSeq) != 2)
                            throw new ArgumentException("Wrong number of arguments to {0}", op.Name);

                        object localArg = RT.second(argAsSeq);
                        Symbol symLocalArg = localArg as Symbol;
                        if (symLocalArg == null || (lb = Compiler.ReferenceLocal(symLocalArg)) == null)
                            throw new ArgumentException("Argument to {0} must be a local variable.", op.Name);

                        paramType = HostArg.ParameterType.ByRef;

                        arg = localArg;
                    }
                }

                Expr expr = Compiler.Analyze(pcon.EvalOrExpr(),arg);

                args.Add(new HostArg(paramType, expr, lb));
            }

            return args;

        }

        #endregion

        #region Expr Members

        public abstract bool HasClrType { get; }
        public abstract Type ClrType { get; }
        public abstract object Eval();
        public abstract void Emit(RHC rhc, ObjExpr objx, CljILGen ilg);

        #endregion

        #region MaybePrimitiveExpr 

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2119:SealMethodsThatSatisfyPrivateInterfaces")]
        public abstract bool CanEmitPrimitive { get; }

        public abstract void EmitUnboxed(RHC rhc, ObjExpr objx, CljILGen ilg);

        #endregion

        #region Reflection helpers

        internal static readonly MethodInfo Method_RT_sbyteCast = typeof(RT).GetMethod("sbyteCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_byteCast = typeof(RT).GetMethod("byteCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_shortCast = typeof(RT).GetMethod("shortCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_ushortCast = typeof(RT).GetMethod("ushortCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_intCast = typeof(RT).GetMethod("intCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uintCast = typeof(RT).GetMethod("uintCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_longCast = typeof(RT).GetMethod("longCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_ulongCast = typeof(RT).GetMethod("ulongCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_floatCast = typeof(RT).GetMethod("floatCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_doubleCast = typeof(RT).GetMethod("doubleCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_charCast = typeof(RT).GetMethod("charCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_decimalCast = typeof(RT).GetMethod("decimalCast", new Type[] { typeof(object) });

        internal static readonly MethodInfo Method_RT_uncheckedSbyteCast = typeof(RT).GetMethod("uncheckedSByteCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedByteCast = typeof(RT).GetMethod("uncheckedByteCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedShortCast = typeof(RT).GetMethod("uncheckedShortCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedUshortCast = typeof(RT).GetMethod("uncheckedUShortCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedIntCast = typeof(RT).GetMethod("uncheckedIntCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedUintCast = typeof(RT).GetMethod("uncheckedUIntCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedLongCast = typeof(RT).GetMethod("uncheckedLongCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedUlongCast = typeof(RT).GetMethod("uncheckedULongCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedFloatCast = typeof(RT).GetMethod("uncheckedFloatCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedDoubleCast = typeof(RT).GetMethod("uncheckedDoubleCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedCharCast = typeof(RT).GetMethod("uncheckedCharCast", new Type[] { typeof(object) });
        internal static readonly MethodInfo Method_RT_uncheckedDecimalCast = typeof(RT).GetMethod("uncheckedDecimalCast", new Type[] { typeof(object) });

        internal static readonly MethodInfo Method_RT_booleanCast = typeof(RT).GetMethod("booleanCast", new Type[] { typeof(object) });

        internal static readonly MethodInfo Method_RT_intPtrCast = typeof (RT).GetMethod("intPtrCast", new Type[] { typeof (object) });
        internal static readonly MethodInfo Method_RT_uintPtrCast = typeof (RT).GetMethod("uintPtrCast", new Type[] { typeof (object) });

        #endregion

        #region Tags and types
        
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily")]
        public static Type MaybeType(object form, bool stringOk)
        {
            if (form is Type)
                return (Type)form;

            Type t = null;
            if (form is Symbol)
            {
                Symbol sym = (Symbol)form;
                // if symbol refers to something in the lexical scope, it's not a type
                if(Compiler.LocalEnvVar.deref() != null && ((IPersistentMap)Compiler.LocalEnvVar.deref()).containsKey(sym))
                    return null;
                if (sym.Namespace == null) // if ns-qualified, can't be classname
                {
                    if (Util.equals(sym, Compiler.CompileStubSymVar.get()))
                        return (Type)Compiler.CompileStubClassVar.get();
                    if (sym.Name.IndexOf('.') > 0 || sym.Name[sym.Name.Length - 1] == ']')  // Array.  JVM version detects [whatever  notation.

                        t = RT.classForNameE(sym.Name);
                    else
                    {
                        object o = Compiler.CurrentNamespace.GetMapping(sym);
                        if (o is Type)
                            t = (Type)o;
                        else
                        {
                            try
                            {
                                t = RT.classForName(sym.Name);
                            }
                            catch (Exception)
                            {
                                // aargh
                                // leave t set to null -> return null
                            }
                        }
                    }

                }
            }
            else if (stringOk && form is string)
                t = RT.classForNameE((string)form);

            return t;
        }

        internal static Type TagToType(object tag)
        {
            Type t = null;

            Symbol sym = tag as Symbol;
            if (sym != null)
            {
                if (sym.Namespace == null) // if ns-qualified, can't be classname
                {
                    switch (sym.Name)
                    {
                        case "objects": 
                            t = typeof(object[]); break;
                        case "ints": 
                            t = typeof(int[]); break;
                        case "longs": t = typeof(long[]); break;
                        case "floats": t = typeof(float[]); break;
                        case "doubles": t = typeof(double[]); break;
                        case "chars": t = typeof(char[]); break;
                        case "shorts": t = typeof(short[]); break;
                        case "bytes": t = typeof(byte[]); break;
                        case "booleans":
                        case "bools": t = typeof(bool[]); break;
                        case "uints": t = typeof(uint[]); break;
                        case "ushorts": t = typeof(ushort[]); break;
                        case "ulongs": t = typeof(ulong[]); break;
                        case "sbytes": t = typeof(sbyte[]); break;
                        case "int":
                        case "Int32":
                        case "System.Int32":
                            t = typeof(int); break;
                        case "long":
                        case "Int64":
                        case "System.Int64": 
                            t = typeof(long); break;
                        case "short":
                        case "Int16":
                        case "System.Int16":
                            t = typeof(short); break;
                        case "byte":
                        case "Byte":
                        case "System.Byte": 
                            t = typeof(byte); break;
                        case "float":
                        case "Single":
                        case "System.Single": 
                            t = typeof(float); break;
                        case "double":
                        case "Double":
                        case "System.Double": 
                            t = typeof(double); break;
                        case "char":
                        case "Char":
                        case "System.Char": 
                        t = typeof(char); break;
                        case "bool":
                        case "boolean":
                        case "Boolean":
                        case "System.Boolean": 
                            t = typeof(bool); break;
                        case "uint":
                        case "UInt32":
                        case "System.UInt32": 
                            t = typeof(uint); break;
                        case "ulong":
                        case "UInt64":
                        case "System.UInt64": 
                            t = typeof(ulong); break;
                        case "ushort":
                        case "UInt16":
                        case "System.UInt16": 
                            t = typeof(ushort); break;
                        case "sbyte":
                        case "SByte":
                        case "System.SByte": 
                            t = typeof(sbyte); break;
                    }
                }
            }

            if(t == null)
                t = MaybeType(tag, true);
            if (t != null)
                return t;

            throw new ArgumentException("Unable to resolve typename: " + tag);
        }

        #endregion

        #region Code generation

        internal static void EmitBoxReturn(ObjExpr objx, CljILGen ilg, Type returnType)

        {
            if (returnType == typeof(void))
                ilg.Emit(OpCodes.Ldnull);
            else if (returnType.IsPrimitive || returnType.IsValueType)
                ilg.Emit(OpCodes.Box, returnType);
        }

        internal static void EmitUnboxArg(ObjExpr objx, CljILGen ilg, Type paramType)
        {
            EmitUnboxArg(ilg, paramType);
        }

        internal static void EmitUnboxArg(CljILGen ilg, Type paramType)
        {
            if (paramType.IsPrimitive)
            {
                MethodInfo m = null;

                if (paramType == typeof(bool))
                {
                    m = HostExpr.Method_RT_booleanCast;
                }
                else if (paramType == typeof(char))
                {
                    m = HostExpr.Method_RT_charCast;
                }
                else if(paramType == typeof(IntPtr))
                {
                    m = HostExpr.Method_RT_intPtrCast;
                }
                else if(paramType == typeof(UIntPtr))
                {
                    m = HostExpr.Method_RT_uintPtrCast;
                }
                else
                {
                    var typeCode = Type.GetTypeCode(paramType);
                    if (RT.booleanCast(RT.UncheckedMathVar.deref()))
                    {
                        switch (typeCode)
                        {
                            case TypeCode.SByte:
                                m = HostExpr.Method_RT_uncheckedSbyteCast;
                                break;
                            case TypeCode.Byte:
                                m = HostExpr.Method_RT_uncheckedByteCast;
                                break;
                            case TypeCode.Int16:
                                m = HostExpr.Method_RT_uncheckedShortCast;
                                break;
                            case TypeCode.UInt16:
                                m = HostExpr.Method_RT_uncheckedUshortCast;
                                break;
                            case TypeCode.Int32:
                                m = HostExpr.Method_RT_uncheckedIntCast;
                                break;
                            case TypeCode.UInt32:
                                m = HostExpr.Method_RT_uncheckedUintCast;
                                break;
                            case TypeCode.Int64:
                                m = HostExpr.Method_RT_uncheckedLongCast;
                                break;
                            case TypeCode.UInt64:
                                m = HostExpr.Method_RT_uncheckedUlongCast;
                                break;
                            case TypeCode.Single:
                                m = HostExpr.Method_RT_uncheckedFloatCast;
                                break;
                            case TypeCode.Double:
                                m = HostExpr.Method_RT_uncheckedDoubleCast;
                                break;
                            case TypeCode.Char:
                                m = HostExpr.Method_RT_uncheckedCharCast;
                                break;
                            case TypeCode.Decimal:
                                m = HostExpr.Method_RT_uncheckedDecimalCast;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException("paramType", paramType, string.Format("Don't know how to handle typeCode {0} for paramType", typeCode));
                        }
                    }
                    else
                    {
                        switch (typeCode)
                        {
                            case TypeCode.SByte:
                                m = HostExpr.Method_RT_sbyteCast;
                                break;
                            case TypeCode.Byte:
                                m = HostExpr.Method_RT_byteCast;
                                break;
                            case TypeCode.Int16:
                                m = HostExpr.Method_RT_shortCast;
                                break;
                            case TypeCode.UInt16:
                                m = HostExpr.Method_RT_ushortCast;
                                break;
                            case TypeCode.Int32:
                                m = HostExpr.Method_RT_intCast;
                                break;
                            case TypeCode.UInt32:
                                m = HostExpr.Method_RT_uintCast;
                                break;
                            case TypeCode.Int64:
                                m = HostExpr.Method_RT_longCast;
                                break;
                            case TypeCode.UInt64:
                                m = HostExpr.Method_RT_ulongCast;
                                break;
                            case TypeCode.Single:
                                m = HostExpr.Method_RT_floatCast;
                                break;
                            case TypeCode.Double:
                                m = HostExpr.Method_RT_doubleCast;
                                break;
                            case TypeCode.Char:
                                m = HostExpr.Method_RT_charCast;
                                break;
                            case TypeCode.Decimal:
                                m = HostExpr.Method_RT_decimalCast;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException("paramType", paramType, string.Format("Don't know how to handle typeCode {0} for paramType", typeCode));
                        }
                    }
                }

                ilg.Emit(OpCodes.Castclass, typeof(Object));
                ilg.Emit(OpCodes.Call,m);
            }
            else
            {
                // TODO: Properly handle value types here.  Really, we need to know the incoming type.
                if (paramType.IsValueType)
                {
                    ilg.Emit(OpCodes.Unbox_Any, paramType);
                }
                else
                    ilg.Emit(OpCodes.Castclass, paramType);
            }
        }

        public bool HasNormalExit() { return true; }

        #endregion
    }
}
