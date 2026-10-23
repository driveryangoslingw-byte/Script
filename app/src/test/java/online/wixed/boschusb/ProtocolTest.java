package online.wixed.boschusb;

import org.junit.Test;
import static org.junit.Assert.*;

public final class ProtocolTest {
    @Test public void handshakeMatchesObservedBytes() {
        byte[] b = Protocol.handshake();
        assertEquals(64, b.length);
        assertEquals("00 00 03 03 02 01", Protocol.hex(
                new byte[]{b[0], b[1], b[2], b[3], b[4], b[5]}));
    }

    @Test public void didRequestMatchesExistingResearchFrame() {
        byte[] b = Protocol.readDid(0x3A, 0x0220, 0);
        assertEquals(64, b.length);
        byte[] expected = {2, 0, 7, 0, (byte)255, (byte)255, 11, 0, 0, 0,
                           0x3A, 4, 3, 0x22, 2, 0x20};
        for (int i = 0; i < expected.length; i++) assertEquals(i + "", expected[i], b[i]);
    }

    @Test public void testerPresentUsesSameEncoder() {
        byte[] b = Protocol.testerPresent(0x20, 1);
        assertEquals(2, b[0]);
        assertEquals(1, b[1]);
        assertEquals(10, b[6]);
        assertEquals(0x20, b[10]);
        assertEquals(3, b[11]);
        assertEquals(2, b[12]);
        assertEquals(0x3E, b[13]);
        assertEquals(0, b[14]);
    }

    @Test public void emptyFrameAndVariants() {
        byte[] b = Protocol.initial();
        assertEquals(64, b.length);
        assertEquals(2, b[0]);
        assertEquals(0, b[1]);
        assertEquals(7, b[6]);
        assertFalse(Protocol.Variant.DIRECT.initial);
        assertEquals(0, Protocol.Variant.DIRECT.testerSeq);
        assertEquals(1, Protocol.Variant.EMPTY_NEXT.testerSeq);
        assertEquals(0, Protocol.Variant.EMPTY_SAME.testerSeq);
    }

    @Test public void transportErrorIsNotUdsNegative() {
        byte[] b = new byte[64];
        b[1] = (byte)0xFF;
        assertEquals("TRANSPORT_00_FF", Protocol.analyze(b, 0x22, 0x0220).kind);
    }

    @Test public void handshakeIsRecognized() {
        byte[] b = new byte[64];
        b[1] = 1; b[2] = 2;
        assertTrue(Protocol.isHandshake(b));
        assertEquals("HANDSHAKE_A", Protocol.analyze(b, 0, 0).kind);
    }

    @Test public void positiveTesterPatternIsOnlyCandidate() {
        byte[] b = new byte[64];
        b[0] = 2; b[11] = 3; b[12] = 2; b[13] = 0x7E; b[14] = 0;
        Protocol.Reply r = Protocol.analyze(b, 0x3E, 0);
        assertEquals("UDS_POSITIVE_CANDIDATE", r.kind);
        assertTrue(r.strong);
        assertTrue(r.udsCandidate());
    }

    @Test public void didPositiveRequiresExactDid() {
        byte[] b = new byte[64];
        b[0] = 2; b[11] = 4; b[12] = 3; b[13] = 0x62;
        b[14] = 2; b[15] = 0x21;
        assertEquals("OTHER_FRAME", Protocol.analyze(b, 0x22, 0x0220).kind);
        assertEquals("UDS_POSITIVE_CANDIDATE", Protocol.analyze(b, 0x22, 0x0221).kind);
    }

    @Test public void udsNegativeKeepsNrc() {
        byte[] b = new byte[64];
        b[0] = 2; b[11] = 4; b[12] = 3; b[13] = 0x7F;
        b[14] = 0x3E; b[15] = 0x22;
        Protocol.Reply r = Protocol.analyze(b, 0x3E, 0);
        assertEquals("UDS_NEGATIVE_CANDIDATE", r.kind);
        assertEquals(0x22, r.nrc);
    }

    @Test public void noWriteOrSessionServices() {
        try {
            Protocol.uds(0x20, 0, new byte[]{0x2E, 0, 0});
            fail("Writes must not be encoded");
        } catch (IllegalArgumentException expected) {}
        try {
            Protocol.uds(0x20, 0, new byte[]{0x10, 0x03});
            fail("Session changes must not be encoded");
        } catch (IllegalArgumentException expected) {}
    }
}
