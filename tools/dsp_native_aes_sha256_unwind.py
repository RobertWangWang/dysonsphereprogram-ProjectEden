"""核验 AES/SHA-256 异常处理器及四种实现的完整展开记录。"""
from dsp_native_sha512_unwind import main
from dsp_native_aes_sha256_match import FUNCTIONS

if __name__ == '__main__':
    main(dict(stem='aesni-sha256', family='aesni-sha256-family', output='aesni-sha256-seh',
        generator='aesni-sha256-x86_64.pl', functions=FUNCTIONS[1:],
        entry=0x1800327c0, end=0x18003292c, handler_offset=0x48a0,
        import_offset=0x49fb, name='aesni_sha256_se_handler'))
