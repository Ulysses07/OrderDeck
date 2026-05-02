interface LogoProps {
  size?: number;
  className?: string;
}

/**
 * OrderDeck logosu — alışveriş sepeti + chat baloncuğu kompozisyonu, mavi gradient.
 * SVG'yi inline render ediyor (HTTP request azaltmak + tema esnekliği için).
 *
 * SVG path /public/logo.svg ile birebir aynı; production'da statik dosyayı
 * <link rel="icon"> için kullanıyoruz, navigasyonda inline rendering tercih.
 */
export function Logo({ size = 28, className }: LogoProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 1254 1254"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      className={className}
    >
      <defs>
        <linearGradient
          id="ord-logo-gradient"
          x1="190"
          y1="260"
          x2="1020"
          y2="980"
          gradientUnits="userSpaceOnUse"
        >
          <stop offset="0" stopColor="#20C5F7" />
          <stop offset="0.45" stopColor="#149CF3" />
          <stop offset="1" stopColor="#0F63F5" />
        </linearGradient>
      </defs>
      <path
        d="M 186.0 325.0 L 180.0 340.0 L 179.0 359.0 L 184.0 376.0 L 197.0 393.0 L 209.0 401.0 L 225.0 406.0 L 289.0 407.0 L 306.0 415.0 L 317.0 427.0 L 324.0 446.0 L 390.0 723.0 L 398.0 746.0 L 407.0 762.0 L 424.0 784.0 L 438.0 797.0 L 472.0 817.0 L 505.0 826.0 L 552.0 827.0 L 561.0 830.0 L 568.0 837.0 L 571.0 844.0 L 571.0 903.0 L 575.0 916.0 L 582.0 924.0 L 592.0 928.0 L 602.0 928.0 L 624.0 919.0 L 665.0 891.0 L 726.0 838.0 L 743.0 829.0 L 753.0 827.0 L 856.0 827.0 L 876.0 824.0 L 901.0 816.0 L 923.0 804.0 L 943.0 788.0 L 958.0 771.0 L 969.0 754.0 L 978.0 735.0 L 987.0 706.0 L 1024.0 548.0 L 1029.0 516.0 L 1029.0 493.0 L 1024.0 467.0 L 1010.0 436.0 L 1000.0 422.0 L 984.0 406.0 L 962.0 391.0 L 938.0 381.0 L 904.0 375.0 L 450.0 375.0 L 439.0 372.0 L 428.0 365.0 L 419.0 355.0 L 407.0 334.0 L 387.0 315.0 L 364.0 303.0 L 342.0 298.0 L 224.0 298.0 L 211.0 302.0 L 201.0 308.0 Z M 480.0 877.0 L 457.0 884.0 L 440.0 899.0 L 431.0 917.0 L 430.0 942.0 L 437.0 960.0 L 450.0 975.0 L 469.0 985.0 L 489.0 987.0 L 511.0 980.0 L 527.0 966.0 L 537.0 947.0 L 539.0 928.0 L 532.0 905.0 L 517.0 888.0 L 499.0 879.0 Z M 832.0 877.0 L 809.0 884.0 L 792.0 899.0 L 785.0 911.0 L 781.0 932.0 L 783.0 947.0 L 787.0 957.0 L 803.0 976.0 L 821.0 985.0 L 841.0 987.0 L 863.0 980.0 L 879.0 966.0 L 889.0 946.0 L 890.0 922.0 L 882.0 902.0 L 869.0 888.0 L 851.0 879.0 Z M 672.0 550.0 L 690.0 550.0 L 706.0 557.0 L 716.0 567.0 L 724.0 584.0 L 725.0 597.0 L 719.0 616.0 L 708.0 629.0 L 689.0 638.0 L 672.0 638.0 L 656.0 631.0 L 644.0 619.0 L 637.0 603.0 L 637.0 585.0 L 642.0 572.0 L 656.0 557.0 Z M 528.0 550.0 L 545.0 550.0 L 563.0 558.0 L 575.0 572.0 L 580.0 585.0 L 580.0 602.0 L 573.0 619.0 L 561.0 631.0 L 545.0 638.0 L 528.0 638.0 L 515.0 633.0 L 502.0 622.0 L 493.0 604.0 L 493.0 584.0 L 499.0 570.0 L 513.0 556.0 Z M 816.0 550.0 L 834.0 550.0 L 850.0 557.0 L 863.0 571.0 L 869.0 588.0 L 867.0 608.0 L 860.0 621.0 L 848.0 632.0 L 833.0 638.0 L 817.0 638.0 L 799.0 630.0 L 786.0 615.0 L 781.0 599.0 L 782.0 583.0 L 789.0 568.0 L 799.0 558.0 Z"
        fill="url(#ord-logo-gradient)"
        fillRule="evenodd"
      />
    </svg>
  );
}
