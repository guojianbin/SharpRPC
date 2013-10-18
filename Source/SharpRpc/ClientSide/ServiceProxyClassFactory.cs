﻿#region License
/*
Copyright (c) 2013 Daniil Rodin of Buhgalteria.Kontur team of SKB Kontur

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using SharpRpc.Codecs;
using SharpRpc.Reflection;
using System.Linq;

namespace SharpRpc.ClientSide
{
    public class ServiceProxyClassFactory : IServiceProxyClassFactory
    {
        struct ParameterNecessity
        {
            public MethodParameterDescription Description;
            public Type ConcreteType;
            public IEmittingCodec Codec;
            public int ArgumentIndex;
        }

        private const int MaxInlinableComplexity = 16;

        private readonly IServiceDescriptionBuilder serviceDescriptionBuilder;
        private readonly ICodecContainer codecContainer;
        private readonly AssemblyBuilder assemblyBuilder;
        private readonly ModuleBuilder moduleBuilder;
        private int classNameDisambiguator = 0;

        public ServiceProxyClassFactory(IServiceDescriptionBuilder serviceDescriptionBuilder, ICodecContainer codecContainer)
        {
            this.serviceDescriptionBuilder = serviceDescriptionBuilder;
            this.codecContainer = codecContainer;
            var appDomain = AppDomain.CurrentDomain;
            assemblyBuilder = appDomain.DefineDynamicAssembly(new AssemblyName("SharpRpcServiceProxies"), AssemblyBuilderAccess.Run);
            moduleBuilder = assemblyBuilder.DefineDynamicModule("SharpRpcServiceProxyModule");
        }

        private static readonly Type[] ConstructorParameterTypes = new[] { typeof(IOutgoingMethodCallProcessor), typeof(string), typeof(TimeoutSettings), typeof(ICodecContainer) };
        private static readonly MethodInfo GetManualCodecForMethod = typeof(ICodecContainer).GetMethod("GetManualCodecFor");
        private static readonly MethodInfo GetTypeFromHandleMethod = typeof(Type).GetMethod("GetTypeFromHandle");
        private static readonly MethodInfo ProcessMethod = typeof(IOutgoingMethodCallProcessor).GetMethod("Process");

        public Func<IOutgoingMethodCallProcessor, string, TimeoutSettings, T> CreateProxyClass<T>()
        {
            var proxyClass = CreateProxyClass(typeof(T), typeof(T), null);
            return (p, s, t) => (T) Activator.CreateInstance(proxyClass, p, s, t, codecContainer);
        }

        private Type CreateProxyClass(Type rootType, Type type, string path)
        {
            var serviceDescription = serviceDescriptionBuilder.Build(type);
            path = path ?? serviceDescription.Name;

            var typeBuilder = moduleBuilder.DefineType("__rpc_proxy_" + type.FullName + "_" + classNameDisambiguator++,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, 
                typeof(object), new[] {type});

            #region Emit Fields
            var processorField = typeBuilder.DefineField("methodCallProcessor", typeof(IOutgoingMethodCallProcessor),
                FieldAttributes.Private | FieldAttributes.InitOnly);
            var scopeField = typeBuilder.DefineField("scope", typeof(string),
                FieldAttributes.Private | FieldAttributes.InitOnly);
            var timeoutSettingsField = typeBuilder.DefineField("timeoutSettings", typeof(TimeoutSettings),
                FieldAttributes.Private | FieldAttributes.InitOnly);
            var codecContainerField = typeBuilder.DefineField("codecContainer", typeof(ICodecContainer),
                FieldAttributes.Private | FieldAttributes.InitOnly);
            var codecsField = typeBuilder.DefineField("codecs", typeof(IManualCodec[]),
                FieldAttributes.Private | FieldAttributes.InitOnly);
            #endregion

            var manualCodecTypes = new List<Type>();

            foreach (var methodDesc in serviceDescription.Methods)
            {
                #region Emit Method
                var methodBuilder = typeBuilder.DefineMethod(methodDesc.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.NewSlot | MethodAttributes.Virtual);
                
                Type[] parameterTypes;
                
                Type retvalType;
                if (methodDesc.GenericParameters.Any())
                {
                    var genericTypeParameterBuilders = methodBuilder.DefineGenericParameters(methodDesc.GenericParameters.Select(x => x.Name).ToArray());
                    parameterTypes = methodDesc.Parameters.Select(x => !x.Type.IsGenericParameter ? x.Type : genericTypeParameterBuilders.Single(y => y.Name == x.Type.Name)).ToArray();

                    retvalType = !methodDesc.ReturnType.IsGenericParameter 
                        ? methodDesc.ReturnType 
                        : genericTypeParameterBuilders.Single(y => y.Name == methodDesc.ReturnType.Name);
                }
                else
                {
                    parameterTypes = methodDesc.Parameters.Select(x => x.Type).ToArray();
                    retvalType = methodDesc.ReturnType;
                }
                var parameterTypesAdjustedForRefs = parameterTypes.Select((x, i) => methodDesc.Parameters[i].Way == MethodParameterWay.Val ? x : x.MakeByRefType()).ToArray();
                methodBuilder.SetParameters(parameterTypesAdjustedForRefs);
                methodBuilder.SetReturnType(retvalType);
                methodBuilder.SetImplementationFlags(MethodImplAttributes.Managed);
                
                var parameters = methodDesc.Parameters.Select((x, i) => new ParameterNecessity
                {
                    Description = x,
                    Codec = x.Type.IsGenericParameter ? null : codecContainer.GetEmittingCodecFor(x.Type),
                    ConcreteType = parameterTypes[i],
                    ArgumentIndex = i + 1
                }).ToArray();

                var requestParameters = parameters
                    .Where(x => x.Description.Way == MethodParameterWay.Val || x.Description.Way == MethodParameterWay.Ref)
                    .ToArray();

                var responseParameters = parameters
                    .Where(x => x.Description.Way == MethodParameterWay.Ref || x.Description.Way == MethodParameterWay.Out)
                    .ToArray();

                bool hasRetval = methodDesc.ReturnType != typeof(void);

                var il = methodBuilder.GetILGenerator();
                var locals = new LocalVariableCollection(il, responseParameters.Any() || hasRetval);

                var requestDataArrayVar = il.DeclareLocal(typeof(byte[]));        // byte[] dataArray

                if (requestParameters.Any())
                {
                    bool hasSizeOnStack = false;
                    foreach (var parameter in requestParameters)
                    {
                        EmitCalculateSize(il, parameter, manualCodecTypes, codecsField);
                        if (hasSizeOnStack)
                            il.Emit(OpCodes.Add);
                        else
                            hasSizeOnStack = true;
                    }

                    il.Emit(OpCodes.Newarr, typeof(byte));                      // dataArray = new byte[stack_0]
                    il.Emit(OpCodes.Stloc, requestDataArrayVar);
                    var pinnedVar =                                             // var pinned dataPointer = pin(dataArray)
                        il.Emit_PinArray(typeof(byte), requestDataArrayVar);
                    il.Emit(OpCodes.Ldloc, pinnedVar);                          // data = dataPointer
                    il.Emit(OpCodes.Stloc, locals.DataPointer);

                    foreach (var parameter in requestParameters)
                        EmitEncode(il, locals, parameter, manualCodecTypes, codecsField);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);                                // dataArray = null
                    il.Emit(OpCodes.Stloc, requestDataArrayVar);
                }

                il.Emit_Ldarg(0);                                           // stack_0 = methodCallProcessor
                il.Emit(OpCodes.Ldfld, processorField);
                il.Emit(OpCodes.Ldtoken, rootType);                         // stack_1 = typeof(T)
                il.Emit(OpCodes.Call, GetTypeFromHandleMethod);
                il.Emit(OpCodes.Ldstr, string.Format("{0}/{1}",             // stack_2 = SuperServicePath/ServiceName/MethodName
                    path, methodDesc.Name));
                il.Emit_Ldarg(0);                                           // stack_3 = scope
                il.Emit(OpCodes.Ldfld, scopeField);
                il.Emit(OpCodes.Ldloc, requestDataArrayVar);                // stack_4 = dataArray
                il.Emit_Ldarg(0);                                           // stack_5 = timeoutSettings
                il.Emit(OpCodes.Ldfld, timeoutSettingsField);
                il.Emit(OpCodes.Callvirt, ProcessMethod);                   // stack_0 = stack_0.Process(stack_1, stack_2, stack_3, stack_4, stack_5)

                if (responseParameters.Any() || hasRetval)
                {
                    var responseDataArrayVar = il.DeclareLocal(typeof(byte[]));
                    il.Emit(OpCodes.Stloc, responseDataArrayVar);           // dataArray = stack_0
                    il.Emit(OpCodes.Ldloc, responseDataArrayVar);           // remainingBytes = dataArray.Length
                    il.Emit(OpCodes.Ldlen);
                    il.Emit(OpCodes.Stloc, locals.RemainingBytes);
                    var pinnedVar =                                         // var pinned dataPointer = pin(dataArray)
                        il.Emit_PinArray(typeof(byte), responseDataArrayVar);
                    il.Emit(OpCodes.Ldloc, pinnedVar);                      // data = dataPointer
                    il.Emit(OpCodes.Stloc, locals.DataPointer);

                    foreach (var parameter in responseParameters)
                    {
                        il.Emit(OpCodes.Ldarg, parameter.ArgumentIndex);    // arg_i+1 = decode(data, remainingBytes, false)
                        EmitDecode(il, locals, parameter.Codec, manualCodecTypes, codecsField);
                        il.Emit_Stind(parameter.Description.Type);
                    }

                    if (hasRetval)
                    {
                        var retvalCodec = codecContainer.GetEmittingCodecFor(methodDesc.ReturnType);
                        EmitDecode(il, locals, retvalCodec, manualCodecTypes, codecsField);
                    }
                }
                else
                {
                    il.Emit(OpCodes.Pop);                               // pop(stack_0)
                }

                il.Emit(OpCodes.Ret);
                #endregion
            }

            #region Begin Emit Constructor
            var constructorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, ConstructorParameterTypes);
            var baseConstructor = typeof(object).GetConstructor(Type.EmptyTypes);
            var cil = constructorBuilder.GetILGenerator();
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Call, baseConstructor);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Ldarg_1);
            cil.Emit(OpCodes.Stfld, processorField);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Ldarg_2);
            cil.Emit(OpCodes.Stfld, scopeField);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Ldarg_3);
            cil.Emit(OpCodes.Stfld, timeoutSettingsField);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit_Ldarg(4);
            cil.Emit(OpCodes.Stfld, codecContainerField);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit_Ldc_I4(manualCodecTypes.Count);
            cil.Emit(OpCodes.Newarr, typeof(IManualCodec));
            cil.Emit(OpCodes.Stfld, codecsField);
            for (int i = 0; i < manualCodecTypes.Count; i++)
            {
                var codecType = manualCodecTypes[i];
                cil.Emit_Ldarg(0);
                cil.Emit(OpCodes.Ldfld, codecsField);
                cil.Emit_Ldc_I4(i);
                cil.Emit_Ldarg(4);
                cil.Emit(OpCodes.Call, GetManualCodecForMethod.MakeGenericMethod(codecType));
                cil.Emit(OpCodes.Stelem_Ref);
            }
            #endregion

            foreach (var subserviceDesc in serviceDescription.Subservices)
            {
                #region Emit Subservice Property
                var proxyClass = CreateProxyClass(rootType, subserviceDesc.Service.Type, path + "/" + subserviceDesc.Name);

                var fieldBuilder = typeBuilder.DefineField("_" + subserviceDesc.Name, proxyClass, 
                    FieldAttributes.Private | FieldAttributes.InitOnly);

                cil.Emit_Ldarg(0);
                cil.Emit_Ldarg(1);
                cil.Emit_Ldarg(2);
                cil.Emit_Ldarg(3);
                cil.Emit_Ldarg(4);
                cil.Emit(OpCodes.Newobj, proxyClass.GetConstructor(ConstructorParameterTypes));
                cil.Emit(OpCodes.Stfld, fieldBuilder);

                var methodBuilder = typeBuilder.DefineMethod("get_" + subserviceDesc.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                    subserviceDesc.Service.Type, Type.EmptyTypes);
                methodBuilder.SetImplementationFlags(MethodImplAttributes.Managed);
                var il = methodBuilder.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fieldBuilder);
                il.Emit(OpCodes.Ret);

                var propertyBuilder = typeBuilder.DefineProperty(subserviceDesc.Name, 
                    PropertyAttributes.None, subserviceDesc.Service.Type, Type.EmptyTypes);
                propertyBuilder.SetGetMethod(methodBuilder);
                #endregion
            }

            #region End Emit Constructor
            cil.Emit(OpCodes.Ret);
            #endregion

            return typeBuilder.CreateType();
        }

        private static void EmitCalculateSize(ILGenerator il, ParameterNecessity parameterNecessity, List<Type> manualCodecTypes, FieldBuilder codecsField)
        {
            var codec = parameterNecessity.Codec;

            if (codec.CanBeInlined && codec.EncodingComplexity <= MaxInlinableComplexity)
            {
                switch (parameterNecessity.Description.Way)
                {
                    case MethodParameterWay.Val: codec.EmitCalculateSize(il, parameterNecessity.ArgumentIndex); break;
                    case MethodParameterWay.Ref: codec.EmitCalculateSizeIndirect(il, parameterNecessity.ArgumentIndex, codec.Type); break;
                    default: throw new ArgumentOutOfRangeException("way", string.Format("Unexcepted parameter way '{0}'", parameterNecessity.Description.Way));
                }
            }
            else
            {
                int indexOfCodec = manualCodecTypes.IndexOfFirst(x => x == codec.Type);
                if (indexOfCodec == -1)
                {
                    indexOfCodec = manualCodecTypes.Count;
                    manualCodecTypes.Add(codec.Type);
                }
                var concreteCodecType = typeof(IManualCodec<>).MakeGenericType(codec.Type);
                il.Emit_Ldarg(0);
                il.Emit(OpCodes.Ldfld, codecsField);
                il.Emit_Ldc_I4(indexOfCodec);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Isinst, concreteCodecType);
                switch (parameterNecessity.Description.Way)
                {
                    case MethodParameterWay.Val:
                        il.Emit_Ldarg(parameterNecessity.ArgumentIndex);
                        break;
                    case MethodParameterWay.Ref:
                        il.Emit_Ldarg(parameterNecessity.ArgumentIndex);
                        il.Emit(OpCodes.Ldobj, codec.Type);
                        break;
                    default: throw new ArgumentOutOfRangeException("way", string.Format("Unexcepted parameter way '{0}'", parameterNecessity.Description.Way));
                }
                il.Emit(OpCodes.Callvirt, concreteCodecType.GetMethod("CalculateSize"));
            }
        }

        private static void EmitEncode(ILGenerator il, ILocalVariableCollection locals, ParameterNecessity parameterNecessity, List<Type> manualCodecTypes, FieldBuilder codecsField)
        {
            var codec = parameterNecessity.Codec;

            if (codec.CanBeInlined && codec.EncodingComplexity <= MaxInlinableComplexity)
            {
                switch (parameterNecessity.Description.Way)
                {
                    case MethodParameterWay.Val: codec.EmitEncode(il, locals, parameterNecessity.ArgumentIndex); break;
                    case MethodParameterWay.Ref: codec.EmitEncodeIndirect(il, locals, parameterNecessity.ArgumentIndex, codec.Type); break;
                    default: throw new ArgumentOutOfRangeException("way", string.Format("Unexcepted parameter way '{0}'", parameterNecessity.Description.Way));
                }
            }
            else
            {
                int indexOfCodec = manualCodecTypes.IndexOfFirst(x => x == codec.Type);
                if (indexOfCodec == -1)
                {
                    indexOfCodec = manualCodecTypes.Count;
                    manualCodecTypes.Add(codec.Type);
                }
                var concreteCodecType = typeof(IManualCodec<>).MakeGenericType(codec.Type);
                il.Emit_Ldarg(0);
                il.Emit(OpCodes.Ldfld, codecsField);
                il.Emit_Ldc_I4(indexOfCodec);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Isinst, concreteCodecType);
                il.Emit(OpCodes.Ldloca, locals.DataPointer);
                switch (parameterNecessity.Description.Way)
                {
                    case MethodParameterWay.Val:
                        il.Emit_Ldarg(parameterNecessity.ArgumentIndex);
                        break;
                    case MethodParameterWay.Ref:
                        il.Emit_Ldarg(parameterNecessity.ArgumentIndex);
                        il.Emit(OpCodes.Ldobj, codec.Type);
                        break;
                    default: throw new ArgumentOutOfRangeException("way", string.Format("Unexcepted parameter way '{0}'", parameterNecessity.Description.Way));
                }
                il.Emit(OpCodes.Callvirt, concreteCodecType.GetMethod("Encode"));
            }
        }

        private static void EmitDecode(ILGenerator il, ILocalVariableCollection locals, IEmittingCodec codec, List<Type> manualCodecTypes, FieldBuilder codecsField)
        {
            if (codec.CanBeInlined && codec.EncodingComplexity <= MaxInlinableComplexity)
            {
                codec.EmitDecode(il, locals, false);
            }
            else
            {
                int indexOfCodec = manualCodecTypes.IndexOfFirst(x => x == codec.Type);
                if (indexOfCodec == -1)
                {
                    indexOfCodec = manualCodecTypes.Count;
                    manualCodecTypes.Add(codec.Type);
                }
                var concreteCodecType = typeof(IManualCodec<>).MakeGenericType(codec.Type);
                il.Emit_Ldarg(0);
                il.Emit(OpCodes.Ldfld, codecsField);
                il.Emit_Ldc_I4(indexOfCodec);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Isinst, concreteCodecType);
                il.Emit(OpCodes.Ldloca, locals.DataPointer);
                il.Emit(OpCodes.Ldloca, locals.RemainingBytes);
                il.Emit_Ldc_I4(0);
                il.Emit(OpCodes.Callvirt, concreteCodecType.GetMethod("Decode"));
            }
        }
    }
}
