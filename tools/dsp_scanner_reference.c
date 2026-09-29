/* Hand-reconstructed reference for rail_api.dll 10632090.
 * classification points at the original object's +0x4c byte table.
 * Valid allocated buffers and non-wrapping pointer differences are required.
 * This is not original Ghidra output or a replacement game implementation. */
static unsigned classify(const unsigned char *table, const unsigned char *p) {
    unsigned a = p[0], b = p[1];
    if (a == 0) return table[b];
    if (a >= 0xd8 && a <= 0xdb) return 7;
    if (a >= 0xdc && a <= 0xdf) return 8;
    if (a == 0xff && b >= 0xfe) return 0;
    return 29;
}

__declspec(dllexport) int dsp_scan(const unsigned char *table,
    const unsigned char *begin, const unsigned char *end, const unsigned char **out) {
    const unsigned char *p = begin;
    unsigned kind;
    if (begin == end) return -4;
    if ((end - begin) & 1) {
        if (end - begin == 1) return -1;
        --end;
    }
    kind = classify(table, p);
    switch (kind) {
    case 0: case 1: case 8: *out = p; return 0;
    case 4:
        p += 2;
        if (p == end) return -1;
        if (p[0] == 0 && p[1] == ']') {
            if (begin + 4 == end) return -1;
            if (begin[4] == 0 && begin[5] == '>') {
                *out = begin + 6; return 40;
            }
        }
        break;
    case 5:
        if (end - p < 2) return -2;
        p += 2; break;
    case 6:
        if (end - p < 3) return -2;
        p += 3; break;
    case 7:
        if (end - p < 4) return -2;
        p += 4; break;
    case 9:
        p += 2;
        if (p == end) return -1;
        if (classify(table, p) != 10) p = begin;
        *out = p + 2; return 7;
    case 10: *out = p + 2; return 7;
    default: p += 2; break;
    }
    while (p != end) {
        kind = classify(table, p);
        switch (kind) {
        case 0: case 1: case 4: case 8: case 9: case 10:
            *out = p; return 6;
        case 5: if (end - p < 2) { *out = p; return 6; } p += 2; break;
        case 6: if (end - p < 3) { *out = p; return 6; } p += 3; break;
        case 7: if (end - p < 4) { *out = p; return 6; } p += 4; break;
        default: p += 2; break;
        }
    }
    *out = p; return 6;
}
