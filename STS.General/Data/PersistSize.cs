﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using STS.General.Extensions;
using System.IO;
using STS.General.Compression;
using STS.General.Comparers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace STS.General.Data
{
    public class PersistSize<T>
    {
        public readonly Func<T, int> getSize;

        public readonly Type Type;

        public readonly Func<Type, MemberInfo, int> MembersOrder;
        public readonly AllowNull AllowNull;
        public readonly int CharSize;

        public PersistSize(AllowNull allowNull = AllowNull.None, Func<Type, MemberInfo, int> membersOrder = null)
            : this(1, allowNull, membersOrder)
        {
        }

        public PersistSize(int charSize, AllowNull allowNull = AllowNull.None, Func<Type, MemberInfo, int> membersOrder = null)
        {
            Type = typeof(T);

            CharSize = charSize;
            AllowNull = allowNull;
            MembersOrder = membersOrder;

            getSize = CreateGetSizeMethod().Compile();
        }

        public int GetSize(T obj)
        {
            return getSize(obj);
        }

        public Expression<Func<T, int>> CreateGetSizeMethod()
        {
            var obj = Expression.Parameter(typeof(T), "obj");

            var body = PersistSizeHelper.CreateSizeBody(obj, CharSize, MembersOrder, AllowNull);

            return Expression.Lambda<Func<T, int>>(body, obj);
        }
    }

    public class PersistSizeHelper
    {
        public static Expression CreateSizeBody(Expression item, int charSize, Func<Type, MemberInfo, int> membersOrder, AllowNull allowNull)
        {
            return BuildGetSize(item, charSize, membersOrder, allowNull, 0);
        }

        private static Expression BuildGetSize(Expression item, int charSize, Func<Type, MemberInfo, int> membersOrder, AllowNull allowNull, int depth)
        {
            var type = item.Type;
            bool canBeNull = CanBeNull(type, allowNull, depth);

            if (type == typeof(Guid))
                return GetPrimitiveValueSize(Expression.Call(item, type.GetMethod("ToByteArray")), charSize, false);

            if (type.IsEnum())
                return GetPrimitiveValueSize(Expression.Convert(item, item.Type.GetEnumBaseType()), charSize, canBeNull);

            if (DataType.IsPrimitiveType(type))
                return GetPrimitiveValueSize(item, charSize, canBeNull);

            if (type.IsKeyValuePair())
            {
                var key = BuildGetSize(Expression.PropertyOrField(item, "Key"), charSize, membersOrder, allowNull, depth + 1);
                var value = BuildGetSize(Expression.PropertyOrField(item, "Value"), charSize, membersOrder, allowNull, depth + 1);

                return Expression.Add(key, value);
            }

            if (type.IsArray || type.IsList())
            {
                var listSum = Expression.Variable(typeof(int));
                var count = type.IsArray ? (Expression)Expression.ArrayLength(item) : Expression.Property(item, "Count");
                var writeCount = Expression.Call(typeof(CountCompression).GetMethod("GetSize"), Expression.Convert(count, typeof(ulong)));

                int fixedSize = 0;
                var itemType = type.IsArray ? type.GetElementType() : type.GetGenericArgument(0);
                if (HasFixedSize(itemType, out fixedSize))
                {
                    if (!canBeNull)
                        return Expression.Add(writeCount, Expression.Multiply(count, Expression.Constant(fixedSize)));

                    return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                                  Expression.Add(
                                        Expression.Add(Expression.Constant(1), writeCount),
                                        Expression.Multiply(count, Expression.Constant(fixedSize))),
                                  Expression.Constant(1)
                        );
                }

                if (!canBeNull)
                {
                    return Expression.Block(new ParameterExpression[] { listSum },
                            Expression.Assign(listSum, writeCount),
                            item.For(i =>
                                Expression.AddAssign(listSum, BuildGetSizeWithAssignedOrCurrentVariable(type.IsArray ? Expression.ArrayAccess(item, i) : item.This(i), charSize, membersOrder, allowNull, depth + 1)),
                                Expression.Label()),

                                Expression.Label(Expression.Label(typeof(int)), listSum)
                            );
                }

                return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                                    Expression.Block(new ParameterExpression[] { listSum },
                                        Expression.Assign(listSum, Expression.Constant(1)),
                                        Expression.AddAssign(listSum, writeCount),
                                        item.For(i =>
                                            Expression.AddAssign(listSum, BuildGetSizeWithAssignedOrCurrentVariable(type.IsArray ? Expression.ArrayAccess(item, i) : item.This(i), charSize, membersOrder, allowNull, depth + 1)),
                                            Expression.Label()),

                                            Expression.Label(Expression.Label(typeof(int)), listSum)
                                        ),
                                    Expression.Constant(1, typeof(int))
                                );
            }

            if (type.IsDictionary())
            {
                var keyType = type.GetGenericArgument(0);

                if (!IsSupportDictionaryKeyType(keyType))
                    throw new NotSupportedException(String.Format("Dictionarty<{0}, TValue>", keyType.ToString()));

                var valueType = type.GetGenericArgument(1);
                bool isEnum = type.GetGenericArgument(0).IsEnum();

                if (!DataType.IsPrimitiveType(keyType) && !isEnum && type != typeof(Guid))
                    throw new NotSupportedException(String.Format("Dictionarty<{0}, TValue>", type.GetGenericArgument(0).ToString()));

                var dictSum = Expression.Variable(typeof(int));

                var count = Expression.Property(item, "Count");
                var writeCount = Expression.Call(typeof(CountCompression).GetMethod("GetSize"), Expression.Convert(count, typeof(ulong)));

                int keySize = 0;
                int valueSize = 0;
                if (HasFixedSize(keyType, out keySize) && HasFixedSize(valueType, out valueSize))
                {
                    if (!canBeNull)
                        return Expression.Multiply(writeCount, Expression.Constant(keySize + valueSize));

                    return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                            Expression.Add(
                                Expression.Constant(1),
                                Expression.Multiply(writeCount, Expression.Constant(keySize + valueSize))
                                ),
                            Expression.Constant(1)
                        );
                }

                if (!canBeNull)
                {
                    return Expression.Block(new ParameterExpression[] { dictSum },
                            Expression.Assign(dictSum, writeCount),
                            item.ForEach(current =>
                            {
                                var kv = Expression.Variable(current.Type);

                                return Expression.Block(new ParameterExpression[] { kv },
                                    Expression.Assign(kv, current),

                                    Expression.AddAssign(dictSum, BuildGetSizeWithAssignedOrCurrentVariable(Expression.PropertyOrField(kv, "Key"), charSize, membersOrder, allowNull, depth + 1)),
                                    Expression.AddAssign(dictSum, BuildGetSizeWithAssignedOrCurrentVariable(Expression.PropertyOrField(kv, "Value"), charSize, membersOrder, allowNull, depth + 1))
                                );
                            }, Expression.Label()),

                            Expression.Label(Expression.Label(typeof(int)), dictSum)
                        );
                }

                return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                        Expression.Block(new ParameterExpression[] { dictSum },
                            Expression.Assign(dictSum, Expression.Constant(1)),
                            Expression.Assign(dictSum, writeCount),
                                item.ForEach(current =>
                                {
                                    var kv = Expression.Variable(current.Type);

                                    return Expression.Block(new ParameterExpression[] { kv },
                                        Expression.Assign(kv, current),

                                        Expression.AddAssign(dictSum, BuildGetSizeWithAssignedOrCurrentVariable(Expression.PropertyOrField(kv, "Key"), charSize, membersOrder, allowNull, depth + 1)),
                                        Expression.AddAssign(dictSum, BuildGetSizeWithAssignedOrCurrentVariable(Expression.PropertyOrField(kv, "Value"), charSize, membersOrder, allowNull, depth + 1))
                                    );
                                }, Expression.Label()),

                                Expression.Label(Expression.Label(typeof(int)), dictSum)
                            ),

                            Expression.Constant(1)
                        );
            }

            if (type.IsNullable())
            {
                var getSize = BuildGetSize(Expression.PropertyOrField(item, "Value"), charSize, membersOrder, allowNull, depth + 1);

                if (!canBeNull)
                    return getSize;

                return Expression.Condition(Expression.PropertyOrField(item, "HasValue"),
                        Expression.Add(Expression.Constant(1), getSize),
                        Expression.Constant(1)
                    );
            }

            if (type.IsClass() || type.IsStruct())
            {
                List<ParameterExpression> variables = new List<ParameterExpression>();
                List<Expression> list = new List<Expression>();

                var members = DataTypeUtils.GetPublicMembers(type, membersOrder).ToList();

                Expression megaAdd = Expression.Empty();

                for (int i = 0; i < members.Count; i++)
                {
                    var mType = members[i].GetUnderlyingType();

                    int mSize = 0;
                    if (HasFixedSize(mType, out mSize))
                    {
                        var getSize = BuildGetSize(Expression.PropertyOrField(item, members[i].Name), charSize, membersOrder, allowNull, depth + 1);
                        megaAdd = (i == 0) ? getSize : Expression.Add(megaAdd, getSize);
                    }
                    else
                    {
                        var @var = Expression.Variable(members[i].GetUnderlyingType());
                        variables.Add(var);
                        list.Add(Expression.Assign(var, Expression.PropertyOrField(item, members[i].Name)));

                        var getSize = BuildGetSize(var, charSize, membersOrder, allowNull, depth + 1);
                        megaAdd = (i == 0) ? getSize : Expression.Add(megaAdd, getSize);
                    }
                }

                if (!canBeNull || type.IsStruct())
                {
                    if (list.Count == 0)
                        return megaAdd;

                    list.Add(Expression.Label(Expression.Label(typeof(int)), megaAdd));

                    return Expression.Block(variables, list);
                }

                if (variables.Count == 0)
                {
                    return Expression.Block(
                            Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                                 Expression.Add(Expression.Constant(1), megaAdd),

                            Expression.Constant(1)
                        ));
                }

                list.Add(Expression.Label(Expression.Label(typeof(int)), Expression.Add(Expression.Constant(1), megaAdd)));

                return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                        Expression.Block(variables, list),
                        Expression.Constant(1)
                    );
            }

            throw new NotSupportedException(item.Type.ToString());
        }

        private static Expression BuildGetSizeWithAssignedOrCurrentVariable(Expression variable, int charSize, Func<Type, MemberInfo, int> membersOrder, AllowNull allowNull, int depth)
        {
            var type = variable.Type;

            if (DataType.IsPrimitiveType(type) && type.IsEnum() && type != typeof(Guid))
                return BuildGetSize(variable, charSize, membersOrder, allowNull, depth + 1);

            ParameterExpression @var = Expression.Variable(type);
            return Expression.Block(new ParameterExpression[] { @var },
                    Expression.Assign(@var, variable),

                    Expression.Label(Expression.Label(typeof(int)), BuildGetSize(@var, charSize, membersOrder, allowNull, depth + 1))
                );
        }

        private static Expression GetPrimitiveValueSize(Expression item, int charSize, bool canBeNull)
        {
            Debug.Assert(DataType.IsPrimitiveType(item.Type));

            Type type = item.Type;
            int typeCode;

#if NETFX_CORE
            typeCode = DataType.GetTypeCode(type);
#else
            typeCode = (int)Type.GetTypeCode(type);
#endif
            switch (typeCode)
            {
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.Byte:
                case TypeCode.SByte:
                    {
                        return Expression.Constant(1);
                    }

                case TypeCode.Int16:
                case TypeCode.UInt16:
                    {
                        return Expression.Constant(2);
                    }

                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Single:
                    {
                        return Expression.Constant(4);
                    }

                case TypeCode.DateTime:
                case TypeCode.Decimal:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Double:
                    {
                        return Expression.Constant(8);
                    }

                default:
                    break;
            }

            if (type == typeof(TimeSpan))
            {
                return Expression.Constant(8);
            }

            if (type == typeof(String))
            {
                var stringSize = Expression.Add(
                            Expression.Call(typeof(CountCompression).GetMethod("GetSize"), Expression.ConvertChecked(Expression.Property(item, "Length"), typeof(ulong))),
                            Expression.Multiply(Expression.Constant(charSize), Expression.PropertyOrField(item, "Length"))
                        );

                if (!canBeNull)
                    return stringSize;

                return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                                   Expression.Add(Expression.Constant(1), stringSize),
                                   Expression.Constant(1)
                               );
            }

            if (type == typeof(byte[]))
            {
                var byteArraySize = Expression.Add(
                            Expression.Call(typeof(CountCompression).GetMethod("GetSize"), Expression.ConvertChecked(Expression.Property(item, "Length"), typeof(ulong))),
                            Expression.PropertyOrField(item, "Length")
                        );

                if (!canBeNull)
                    return byteArraySize;

                return Expression.Condition(Expression.NotEqual(item, Expression.Constant(null)),
                                Expression.Add(Expression.Constant(1), byteArraySize),
                                Expression.Constant(1)
                            );
            }

            throw new NotSupportedException(type.ToString());
        }

        /// <summary>
        /// Returns -1 if size is not fixed.
        /// </summary>
        private static bool HasFixedSize(Type type, out int size)
        {
            int typeCode;

#if NETFX_CORE
            typeCode = DataType.GetTypeCode(type);
#else
            typeCode = (int)Type.GetTypeCode(type);
#endif
            switch (typeCode)
            {
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.Byte:
                case TypeCode.SByte:
                    {
                        size = 1;

                        return true;
                    }

                case TypeCode.Int16:
                case TypeCode.UInt16:
                    {
                        size = 2;

                        return true;
                    }

                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Single:
                    {
                        size = 4;

                        return true;
                    }

                case TypeCode.DateTime:
                case TypeCode.Decimal:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Double:
                    {
                        size = 8;

                        return true;
                    }

                default:
                    break;
            }

            if (type == typeof(TimeSpan))
            {
                size = 8;

                return true;
            }

            size = -1;

            return false;
        }

        private static bool CanBeNull(Type type, AllowNull allowNull, int depth)
        {
            //if (type == typeof(Guid))
            //    return false;

            if (type.IsEnum())
                return false;

            if (type.IsStruct() && !type.IsNullable())
                return false;

            if (allowNull == AllowNull.OnlyMembers)
                return depth > 0;

            return allowNull == AllowNull.All;
        }

        private static bool IsSupportDictionaryKeyType(Type type)
        {
            if (type == typeof(Guid))
                return true;

            if (type.IsEnum())
                return true;

            if (DataType.IsPrimitiveType(type))
                return true;

            return false;
        }
    }
}
