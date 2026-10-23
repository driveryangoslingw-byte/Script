package online.wixed.boschusb;

import java.util.Arrays;
import java.util.Locale;

/**
 * Experimental, read-only Bosch BES2 HMI USB framing.
 *
 * Verified on the target hardware: VID:PID 108C:0155, 64-byte interrupt
 * transfers, handshake 00 00 03 03 02 01 -> 00 01 02 (Sliding Window A).
 *
 * NOT verified: the outer data frame's complete semantics, empty-frame
 * transition, sequence progression, CID routing and response layout.
 * UDS response patterns are explicitly marked as candidates.
 */
public final class Protocol {
    private Protocol() {}

    public static final int VID = 0x108C;
    public static final int PID = 0x0155;
    public static final int LEN = 64;

    // Published community heartbeat responders; their ECU identities are unknown.
    public static final int[] CIDS = {0x20, 0x22, 0x39, 0x3A, 0x5D, 0x5E, 0x5F};
    // Keep the default scan small. Never request serial numbers automatically.
    public static final int[] EXTRA_DIDS = {0x0221, 0x0222, 0xF130, 0xF150, 0xF151};

    public enum Variant {
        DIRECT("A · без пустого кадра, Seq 0", false, 0),
        EMPTY_NEXT("B · пустой Seq 0, затем Seq 1", true, 1),
        EMPTY_SAME("C · пустой Seq 0, затем Seq 0", true, 0);

        public final String title;
        public final boolean initial;
        public final int testerSeq;

        Variant(String title, boolean initial, int testerSeq) {
            this.title = title;
            this.initial = initial;
            this.testerSeq = testerSeq;
        }
    }

    public static byte[] handshake() {
        byte[] a = new byte[LEN];
        a[2] = 0x03;
        a[3] = 0x03;
        a[4] = 0x02;
        a[5] = 0x01;
        return a;
    }

    /** Experimental representation of initial frame 0 / CID 0 / empty body. */
    public static byte[] initial() {
        byte[] a = new byte[LEN];
        a[0] = 0x02;
        a[1] = 0;
        a[2] = 0x07;
        a[4] = (byte) 0xFF;
        a[5] = (byte) 0xFF;
        a[6] = 0x07;
        return a;
    }

    /**
     * Encoder preserved from the previous app's observed 0x22 frame.
     * Single-frame UDS only (up to 7 UDS bytes), never a configuration write.
     */
    public static byte[] uds(int cid, int seq, byte[] payload) {
        if (cid < 0 || cid > 255 || seq < 0 || seq > 255)
            throw new IllegalArgumentException("CID/Seq out of range");
        if (payload == null || payload.length < 1 || payload.length > 7)
            throw new IllegalArgumentException("Single-frame UDS length must be 1..7");
        int service = payload[0] & 0xFF;
        if (service != 0x22 && service != 0x3E)
            throw new IllegalArgumentException("Only read DID / TesterPresent are allowed");

        byte[] a = new byte[LEN];
        a[0] = 0x02;
        a[1] = (byte) seq;
        a[2] = 0x07;
        a[4] = (byte) 0xFF;
        a[5] = (byte) 0xFF;
        a[6] = (byte) (8 + payload.length);
        a[10] = (byte) cid;
        a[11] = (byte) (1 + payload.length);
        a[12] = (byte) payload.length; // ISO-TP Single Frame PCI
        System.arraycopy(payload, 0, a, 13, payload.length);
        return a;
    }

    public static byte[] testerPresent(int cid, int seq) {
        return uds(cid, seq, new byte[]{0x3E, 0x00});
    }

    public static byte[] readDid(int cid, int did, int seq) {
        if (did < 0 || did > 0xFFFF) throw new IllegalArgumentException("Bad DID");
        return uds(cid, seq, new byte[]{0x22, (byte) (did >>> 8), (byte) did});
    }

    public static String cidHex(int cid) {
        return String.format(Locale.US, "%02X", cid);
    }

    public static String didHex(int did) {
        return String.format(Locale.US, "%04X", did);
    }

    public static String hex(byte[] data) {
        if (data == null) return "";
        StringBuilder b = new StringBuilder(data.length * 3);
        for (byte x : data) {
            if (b.length() != 0) b.append(' ');
            b.append(String.format(Locale.US, "%02X", x & 0xFF));
        }
        return b.toString();
    }

    public static String didLabel(int did) {
        switch (did) {
            case 0x0220: return "BUI software (candidate)";
            case 0x0221: return "BUI hardware (candidate)";
            case 0x0222: return "BUI production date (candidate)";
            case 0xF130: return "Component number (candidate)";
            case 0xF150: return "Hardware version (candidate)";
            case 0xF151: return "Software version (candidate)";
            default: return "Raw DID, meaning unverified";
        }
    }

    public static final class Reply {
        public final String kind;
        public final String note;
        public final int offset;
        public final int nrc;
        public final boolean strong;

        private Reply(String kind, String note, int offset, int nrc, boolean strong) {
            this.kind = kind;
            this.note = note;
            this.offset = offset;
            this.nrc = nrc;
            this.strong = strong;
        }

        public boolean udsCandidate() {
            return kind.equals("UDS_POSITIVE_CANDIDATE")
                || kind.equals("UDS_NEGATIVE_CANDIDATE");
        }

        public int rank() {
            if (udsCandidate()) return strong ? 100 : 90;
            if (kind.equals("ISO_TP_FIRST_FRAME_CANDIDATE")) return 75;
            if (kind.equals("HANDSHAKE_A")) return 70;
            if (kind.equals("TRANSPORT_00_FF")) return 60;
            if (kind.equals("OTHER_FRAME")) return 20;
            return 10;
        }

        @Override public String toString() {
            return kind + " — " + note;
        }
    }

    private static Reply reply(String kind, String note) {
        return new Reply(kind, note, -1, -1, false);
    }

    public static Reply timeout() {
        return reply("TIMEOUT", "нет входящего USB-пакета");
    }

    /**
     * Decode only plausible ISO-TP bodies. A UDS-looking byte sequence inside
     * proprietary framing is not proof of a valid ECU response.
     */
    public static Reply analyze(byte[] b, int service, int did) {
        if (b == null || b.length == 0) return timeout();
        if (b.length >= 2 && b[0] == 0 && (b[1] & 0xFF) == 0xFF)
            return reply("TRANSPORT_00_FF", "отказ/статус USB-транспорта, не UDS NRC");
        if (b.length >= 3 && b[0] == 0 && b[1] == 1 && b[2] == 2)
            return reply("HANDSHAKE_A", "подтверждён Sliding Window A");
        boolean zeros = true;
        for (byte x : b) if (x != 0) { zeros = false; break; }
        if (zeros) return reply("ZERO_FRAME", "все байты нулевые");

        // Strongest candidate: match the request-side wrapper layout.
        if (b.length >= 15 && (b[0] & 0xFF) == 2) {
            Reply r = decodeAt(b, 12, service, did, true);
            if (r != null) return r;
        }

        // Fallback: only search the likely body, never the outer header.
        for (int pos = 11; pos < Math.min(b.length - 2, 32); pos++) {
            Reply r = decodeAt(b, pos, service, did, false);
            if (r != null) return r;
        }
        return reply("OTHER_FRAME", "неизвестный формат; см. полный HEX");
    }

    private static Reply decodeAt(byte[] b, int pos, int service, int did, boolean strict) {
        int pci = b[pos] & 0xFF;
        if ((pci & 0xF0) == 0x10) {
            if (pos + 1 < b.length)
                return new Reply("ISO_TP_FIRST_FRAME_CANDIDATE",
                    "возможный длинный ответ; flow control не отправляется", pos, -1, strict);
            return null;
        }
        if ((pci & 0xF0) != 0) return null;
        int size = pci & 0x0F;
        if (size < 2 || size > 7 || pos + size >= b.length) return null;
        if (strict && (b[11] & 0xFF) != size + 1) return null;

        int sid = b[pos + 1] & 0xFF;
        if (sid == 0x7F && size >= 3
                && (b[pos + 2] & 0xFF) == service) {
            int nrc = b[pos + 3] & 0xFF;
            return new Reply("UDS_NEGATIVE_CANDIDATE",
                "7F " + String.format(Locale.US, "%02X %02X", service, nrc)
                + " (UDS-паттерн; обёртка ещё не подтверждена)", pos, nrc, strict);
        }
        if (service == 0x3E && sid == 0x7E && (b[pos + 2] & 0xFF) == 0) {
            return new Reply("UDS_POSITIVE_CANDIDATE",
                "7E 00 (UDS-паттерн; обёртка ещё не подтверждена)", pos, -1, strict);
        }
        if (service == 0x22 && size >= 3 && sid == 0x62
                && (b[pos + 2] & 0xFF) == ((did >>> 8) & 0xFF)
                && (b[pos + 3] & 0xFF) == (did & 0xFF)) {
            return new Reply("UDS_POSITIVE_CANDIDATE",
                "62 " + didHex(did) + " (совпадение DID; значение ещё не декодировано)",
                pos, -1, strict);
        }
        return null;
    }

    public static boolean isHandshake(byte[] b) {
        return b != null && b.length >= 3 && b[0] == 0 && b[1] == 1 && b[2] == 2;
    }
}
