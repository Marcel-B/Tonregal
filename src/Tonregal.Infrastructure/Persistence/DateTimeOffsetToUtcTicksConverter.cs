// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tonregal.Infrastructure.Persistence;

public sealed class DateTimeOffsetToUtcTicksConverter()
    : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
