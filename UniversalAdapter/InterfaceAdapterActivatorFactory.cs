﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace UniversalAdapter
{
    internal sealed class InterfaceAdapterActivatorFactory
    {
        private readonly ModuleBuilder _module;

        internal InterfaceAdapterActivatorFactory(ModuleBuilder module)
        {
            _module = module;
        }

        internal InterfaceAdapterActivator Create(Type interfaceType)
        {
            if (interfaceType == null)
                throw new ArgumentNullException(nameof(interfaceType));
            if (interfaceType.IsInterface == false)
                throw new ArgumentException($"{interfaceType.Name} is not an interface", nameof(interfaceType));

            var typeBuilder = _module.DefineType($"Adapter_{interfaceType.Name}_{interfaceType.GetHashCode()}",
                TypeAttributes.Public, typeof(object));

            // Implement interface
            typeBuilder.AddInterfaceImplementation(interfaceType);

            // Implement copy any generic types
            if (interfaceType.IsGenericType)
            {
                typeBuilder.DefineGenericParameters
                (
                    interfaceType
                        .GetGenericArguments()
                        .Select(a => a.Name)
                        .ToArray()
                );
            }

            // Initialise field to hold reference to injected adapter
            var fieldName = "_" + nameof(IInterfaceHandler).Substring(1).ToCamelCase();
            var field = typeBuilder.DefineField(fieldName, typeof(IInterfaceHandler), FieldAttributes.Private);

            // Get references to properties
            var properties = interfaceType.GetProperties();

            // Build a list of the methods that implement those properties
            var propertyMethods = properties
                .SelectMany(p => new[] { p.GetMethod, p.SetMethod })
                .Where(x => x != null)
                .ToList();

            // Get all methods, except those that implement property getters and setters
            var methods = interfaceType.GetMethods()
                .Where(x => propertyMethods.Contains(x) == false)
                .ToArray();

            // Implement the rest of the functionality
            var propFields = ImplementPropertyAdapters(typeBuilder, field, properties);
            var methodFields = ImplementMethodAdapters(typeBuilder, field, methods);
            var allFields = propFields.Concat(methodFields).ToArray();

            ImplementConstructor(typeBuilder, field, allFields);

            // Return the adapter instance
            var generatedType = typeBuilder.CreateType();
            if (generatedType.ContainsGenericParameters)
            {
                generatedType = generatedType.MakeGenericType(interfaceType.GenericTypeArguments);
            }

            var args = new List<object>(properties.Length + methods.Length);
            args.AddRange(properties);
            args.AddRange(methods);

            return new InterfaceAdapterActivator(generatedType, args);
        }

        /// <summary>
        /// Creates constructor with the following signature:
        /// Ctor([PropertyInfo p1, ... PropertyInfo pN,] [MethodInfo m1, ... MethodInfo pN,] IInterfaceHandler adapter)
        /// </summary>
        private static void ImplementConstructor(
            TypeBuilder type, FieldInfo adapterField, FieldInfo[] fields)
        {
            var parameterTypes = new Type[fields.Length + 1];
            for (var i = 0; i < fields.Length; i++)
            {
                parameterTypes[i] = fields[i].FieldType;
            }
            parameterTypes[fields.Length] = typeof(IInterfaceHandler);

            var ctor = type.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName, CallingConventions.Standard, parameterTypes);
            var baseCtor = typeof(object).GetConstructor(
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance, null, new Type[0], null);

            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);

            for (var i = 0; i < fields.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Stfld, fields[i]);
            }

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg, fields.Length + 1);
            il.Emit(OpCodes.Stfld, adapterField);

            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Implement adapters for all getters and setters on properties
        /// </summary>
        private static List<FieldInfo> ImplementPropertyAdapters(
            TypeBuilder type, FieldBuilder adapterField, PropertyInfo[] properties)
        {
            var getProperty =
                typeof(IInterfaceHandler).GetMethod(nameof(IInterfaceHandler.GetProperty), new[] { typeof(PropertyInfo) });
            var setProperty = typeof(IInterfaceHandler).GetMethod(nameof(IInterfaceHandler.SetProperty),
                new[] { typeof(PropertyInfo), typeof(object) });

            var fields = new List<FieldInfo>();
            foreach (var p in properties)
            {
                // Create a static field to store the PropertyInfo so it doesn't have to be reflected at runtime
                var field = type.DefineField($"_{p.Name.ToCamelCase()}_{p.GetHashCode()}_{nameof(PropertyInfo)}",
                    typeof(PropertyInfo), FieldAttributes.Private);

                var property = type.DefineProperty(p.Name, PropertyAttributes.None, p.PropertyType, new Type[0]);

                if (p.CanRead)
                {
                    var getter = type.DefineMethod("get_" + p.Name,
                        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.Virtual,
                        p.PropertyType, new Type[0]);

                    var il = getter.GetILGenerator(512);

                    // Define all variables that will be needed in this function
                    var ret = il.DeclareLocal(p.PropertyType);

                    // Call "GetProperty"
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, adapterField);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, field);
                    il.EmitCall(OpCodes.Callvirt, getProperty, null);
                    if (p.PropertyType.IsValueType) il.Emit(OpCodes.Unbox_Any, p.PropertyType);
                    if (p.PropertyType.IsClass) il.Emit(OpCodes.Castclass, p.PropertyType);
                    il.Emit(OpCodes.Stloc, ret.LocalIndex);

                    // Return value
                    il.Emit(OpCodes.Ldloc, ret.LocalIndex);
                    il.Emit(OpCodes.Ret);
                    property.SetGetMethod(getter);
                    type.DefineMethodOverride(getter, p.GetGetMethod());
                }

                if (p.CanWrite)
                {
                    var setter = type.DefineMethod("set_" + p.Name,
                        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.Virtual, null,
                        new Type[] { p.PropertyType });
                    var il = setter.GetILGenerator(512);

                    var obj = il.DeclareLocal(typeof(object));
                    var ret = il.DeclareLocal(p.PropertyType);

                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stloc, obj.LocalIndex);

                    // Call "SetProperty"
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, adapterField);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Ldloc, obj.LocalIndex);
                    il.EmitCall(OpCodes.Callvirt, setProperty, null);
                    il.Emit(OpCodes.Stloc, ret.LocalIndex);

                    il.Emit(OpCodes.Ret);
                    property.SetSetMethod(setter);
                    type.DefineMethodOverride(setter, p.GetSetMethod());
                }

                fields.Add(field);
            }

            return fields;
        }

        /// <summary>
        /// Implement adapters for all methods
        /// </summary>
        private static List<FieldInfo> ImplementMethodAdapters(
            TypeBuilder type, FieldBuilder adapterField, MethodInfo[] methods)
        {
            var sendMethod = typeof(IInterfaceHandler).GetMethod(nameof(IInterfaceHandler.Method),
                new[] { typeof(MethodInfo), typeof(object[]) });

            var fields = new List<FieldInfo>();
            foreach (var m in methods)
            {
                // Create a static field to store the MethodInfo so it doesn't have to be reflected at runtime
                var field = type.DefineField($"_{m.Name.ToCamelCase()}_{m.GetHashCode()}_{nameof(MethodInfo)}",
                    typeof(MethodInfo), FieldAttributes.Private);

                var returnType = m.ReturnType;
                var isVoidReturnType = returnType == typeof(void);

                var args = m.GetParameters();
                var argTypes = args.Select(p => p.ParameterType).ToArray();

                var method = type.DefineMethod(m.Name,
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual |
                    MethodAttributes.NewSlot, m.ReturnType, argTypes);

                if (m.IsGenericMethod)
                {
                    var genericArguments = m.GetGenericArguments();
                    method.DefineGenericParameters(genericArguments.Select(x => x.Name).ToArray());
                }

                for (var i = 0; i < args.Length; i++)
                {
                    method.DefineParameter(i, args[i].Attributes, args[i].Name);
                }

                var il = method.GetILGenerator(512);

                // Define all variables that will be needed in this function
                var arr = il.DeclareLocal(typeof(object[]));
                var ret = il.DeclareLocal(isVoidReturnType ? typeof(object) : returnType);

                // Build an object array from parameters
                il.Emit(OpCodes.Ldc_I4, argTypes.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                for (var i = 0; i < argTypes.Length; i++)
                {
                    var arg = argTypes[i];

                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (arg.IsValueType) il.Emit(OpCodes.Box, arg);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Stloc, arr.LocalIndex);

                // Call "Method"
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, adapterField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Ldloc, arr.LocalIndex);
                il.EmitCall(OpCodes.Callvirt, sendMethod, null);
                if (isVoidReturnType == false)
                {
                    if (returnType.IsGenericMethodParameter) il.Emit(OpCodes.Unbox_Any, returnType);
                    else if (returnType.IsValueType) il.Emit(OpCodes.Unbox_Any, returnType);
                    else if (returnType.IsClass) il.Emit(OpCodes.Castclass, returnType);
                }

                il.Emit(OpCodes.Stloc, ret.LocalIndex);

                // Return value
                if (isVoidReturnType == false) il.Emit(OpCodes.Ldloc, ret.LocalIndex);
                il.Emit(OpCodes.Ret);

                fields.Add(field);
            }

            return fields;
        }
    }
}