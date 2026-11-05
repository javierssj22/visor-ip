package com.visorip.tv;

import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONObject;

final class AccessVault {
    private static final String ALIAS="visor-ip-access-v1";
    private static SecretKey key(boolean create) throws Exception {
        KeyStore ks=KeyStore.getInstance("AndroidKeyStore");ks.load(null);
        if(!ks.containsAlias(ALIAS)){
            if(!create)throw new IllegalStateException("No se pudo recuperar el acceso guardado. Ingresalo de nuevo.");
            KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build());
            generator.generateKey();
        }
        return (SecretKey)ks.getKey(ALIAS,null);
    }
    static void save(SharedPreferences prefs,String user,String password) throws Exception {
        JSONObject data=new JSONObject();data.put("user",user);data.put("password",password);
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key(true));
        byte[] clear=data.toString().getBytes(StandardCharsets.UTF_8);
        try{
            String encrypted=Base64.encodeToString(cipher.doFinal(clear),Base64.NO_WRAP),iv=Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP);
            if(!prefs.edit().putString("access",encrypted).putString("access_iv",iv).commit())throw new IllegalStateException("No se pudo guardar el acceso en el TV.");
        }finally{java.util.Arrays.fill(clear,(byte)0);}
    }
    static NetSupport.Login load(SharedPreferences prefs) throws Exception {
        String value=prefs.getString("access","");if(value.isEmpty())return null;
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(false),new GCMParameterSpec(128,Base64.decode(prefs.getString("access_iv",""),Base64.NO_WRAP)));
        byte[] clear=cipher.doFinal(Base64.decode(value,Base64.NO_WRAP));
        try{JSONObject data=new JSONObject(new String(clear,StandardCharsets.UTF_8));return new NetSupport.Login(data.getString("user"),data.getString("password"));}finally{java.util.Arrays.fill(clear,(byte)0);}
    }
}
